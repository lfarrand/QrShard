using System.IO.Compression;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

internal sealed record EncodeOptions
{
    public int Width { get; init; } = 2160;
    public int Height { get; init; } = 2160;
    public int CellPx { get; init; } = 3;
    public int BitsPerCell { get; init; } = 4;
    public int EccParity { get; init; } = 16; // corrects 8 damaged bytes per 255-byte codeword
    public bool Compress { get; init; } = true;
    public int RecoveryPercent { get; init; } // extra parity images (% of data images); 0 = off
}

internal sealed record EncodeResult(
    int ImageCount, long BytesPerImage, int Width, int Height, List<string> Files,
    int DataImages, int ParityImages, int StripeData, int StripeParity);

internal static class Encoder
{
    public const long MaxFileBytes = 1_500_000_000; // byte[] limits; also far beyond any sane shard count
    public const int MaxRecoveryPercent = 100;

    public static EncodeResult Encode(string filePath, string outDir, EncodeOptions opt, Action<string>? log = null)
    {
        if (opt.RecoveryPercent is < 0 or > MaxRecoveryPercent)
            throw new ArgumentException($"Recovery percent must be between 0 and {MaxRecoveryPercent}.");
        if (new FileInfo(filePath).Length > MaxFileBytes)
            throw new InvalidOperationException($"Files larger than {MaxFileBytes / 1_000_000:N0} MB are not supported.");
        byte[] original = File.ReadAllBytes(filePath);
        byte[] sha = SHA256.HashData(original);
        string fileName = Path.GetFileName(filePath);

        byte flags = 0;
        byte[] data = original;
        if (opt.Compress && original.Length > 0 && LooksCompressible(original))
        {
            byte[] compressed = Deflate(original, CompressionLevel.Optimal);
            if (compressed.Length < original.Length)
            {
                data = compressed;
                flags |= ShardHeader.FlagCompressed;
            }
        }

        var layout = Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity);
        long capacityLong = layout.UsableBytes - ShardHeader.Size(fileName);
        if (capacityLong < 1)
            throw new InvalidOperationException("Image capacity is too small for the header; increase resolution or density.");
        int capacity = (int)capacityLong;

        int count = Math.Max(1, (int)((data.LongLength + capacity - 1) / capacity));
        var (stripeData, stripeParity) = PlanStripes(count, opt.RecoveryPercent);
        int stripes = stripeParity > 0 ? (count + stripeData - 1) / stripeData : 0;
        int parityTotal = stripes * stripeParity;

        ulong fileId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var palette = Palette.Build(opt.BitsPerCell);
        byte[] metaModules = layout.PackMetadata();

        Directory.CreateDirectory(outDir);
        int dataPad = Math.Max(3, count.ToString().Length);

        // A padded (to `capacity`) view of data chunk i — the unit the cross-shard code operates on.
        byte[] Chunk(int i)
        {
            long offset = (long)i * capacity;
            int len = (int)Math.Min(capacity, data.LongLength - offset);
            var c = new byte[capacity];
            data.AsSpan((int)offset, len).CopyTo(c);
            return c;
        }

        // Compute parity chunks stripe-by-stripe (bounded memory), before rendering.
        var parityChunks = new byte[parityTotal][];
        if (stripeParity > 0)
        {
            for (int g = 0; g < stripes; g++)
            {
                int first = g * stripeData;
                int s = Math.Min(stripeData, count - first);
                var dataShards = new byte[s][];
                for (int t = 0; t < s; t++)
                    dataShards[t] = Chunk(first + t);
                byte[][] parity = CrossShardFec.Encode(dataShards, stripeParity, capacity);
                for (int p = 0; p < stripeParity; p++)
                    parityChunks[g * stripeParity + p] = parity[p];
            }
        }

        var dataFiles = new string[count];
        var parityFiles = new string[parityTotal];
        int done = 0, totalImages = count + parityTotal;

        int degree = (long)layout.Width * layout.Height > 8_000_000
            ? Math.Min(4, Environment.ProcessorCount)
            : Environment.ProcessorCount;
        var po = new ParallelOptions { MaxDegreeOfParallelism = degree };

        // Data images.
        Parallel.For(0, count, po, i =>
        {
            long offset = (long)i * capacity;
            int payloadLen = (int)Math.Min(capacity, data.LongLength - offset);
            var payload = data.AsSpan((int)offset, payloadLen);

            var header = new ShardHeader
            {
                FileId = fileId,
                Index = i,
                Count = count,
                PayloadLength = payloadLen,
                PayloadCrc32 = Crc.Crc32(payload),
                TotalLength = data.LongLength,
                OriginalLength = original.LongLength,
                Flags = flags,
                Sha256 = sha,
                FileName = fileName,
                StripeData = stripeData,
                StripeParity = stripeParity,
            };

            string outPath = Path.Combine(outDir, $"{fileName}.qrs{(i + 1).ToString().PadLeft(dataPad, '0')}of{count.ToString().PadLeft(dataPad, '0')}.png");
            RenderShard(layout, palette, metaModules, header, payload, outPath);
            dataFiles[i] = outPath;
            int finished = Interlocked.Increment(ref done);
            log?.Invoke($"  [{finished}/{totalImages}] {Path.GetFileName(outPath)} ({payloadLen:N0} bytes)");
        });

        // Parity images.
        Parallel.For(0, parityTotal, po, ord =>
        {
            byte[] parity = parityChunks[ord];
            var header = new ShardHeader
            {
                FileId = fileId,
                Index = ord,
                Count = count,
                PayloadLength = parity.Length,
                PayloadCrc32 = Crc.Crc32(parity),
                TotalLength = data.LongLength,
                OriginalLength = original.LongLength,
                Flags = (byte)(flags | ShardHeader.FlagParity),
                Sha256 = sha,
                FileName = fileName,
                StripeData = stripeData,
                StripeParity = stripeParity,
            };

            string outPath = Path.Combine(outDir, $"{fileName}.qrs-parity{(ord + 1).ToString().PadLeft(3, '0')}of{parityTotal.ToString().PadLeft(3, '0')}.png");
            RenderShard(layout, palette, metaModules, header, parity, outPath);
            parityFiles[ord] = outPath;
            int finished = Interlocked.Increment(ref done);
            log?.Invoke($"  [{finished}/{totalImages}] {Path.GetFileName(outPath)} (parity)");
        });

        var files = new List<string>(count + parityTotal);
        files.AddRange(dataFiles);
        files.AddRange(parityFiles);
        return new EncodeResult(totalImages, capacity, layout.Width, layout.Height, files,
            count, parityTotal, stripeData, stripeParity);
    }

    /// <summary>
    /// Chooses stripe geometry for a given recovery target. Parity is a percentage of the data
    /// images per stripe; the stripe size is capped so data + parity fits one GF(2^8) block (255).
    /// </summary>
    internal static (int StripeData, int StripeParity) PlanStripes(int count, int recoveryPercent)
    {
        if (recoveryPercent <= 0 || count < 1)
            return (0, 0);

        // Largest stripe whose data+parity still fits 255: S * (1 + r/100) <= 255.
        int maxData = (int)Math.Floor(CrossShardFec.MaxShardsPerStripe / (1.0 + recoveryPercent / 100.0));
        int stripeData = Math.Clamp(Math.Min(count, maxData), 1, CrossShardFec.MaxShardsPerStripe - 1);
        int stripeParity = Math.Max(1, (int)Math.Ceiling(stripeData * recoveryPercent / 100.0));
        while (stripeData + stripeParity > CrossShardFec.MaxShardsPerStripe && stripeData > 1)
            stripeData--;
        return (stripeData, stripeParity);
    }

    private static void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, ShardHeader header, ReadOnlySpan<byte> payload, string outPath)
    {
        byte[] headerBytes = header.Serialize();
        byte[] stream = new byte[headerBytes.Length + payload.Length];
        headerBytes.CopyTo(stream, 0);
        payload.CopyTo(stream.AsSpan(headerBytes.Length));

        byte[] cellBuffer = layout.EccParity > 0
            ? Fec.Protect(stream, layout.EccParity, layout.CodewordCount)
            : stream;
        Render(layout, palette, metaModules, cellBuffer, outPath);
    }

    private static void Render(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] cellBuffer, string outPath)
    {
        int w = layout.Width, h = layout.Height;
        var px = new Rgb24[w * h];
        var white = new Rgb24(255, 255, 255);
        var black = new Rgb24(0, 0, 0);
        Array.Fill(px, white);

        // Locator frame ring.
        int f0 = Layout.QuietPx, f1 = Layout.Border; // frame spans [f0, f1) from each edge
        FillRect(px, w, f0, f0, w - 2 * f0, Layout.FramePx, black);                    // top
        FillRect(px, w, f0, h - f1, w - 2 * f0, Layout.FramePx, black);                // bottom
        FillRect(px, w, f0, f0, Layout.FramePx, h - 2 * f0, black);                    // left
        FillRect(px, w, w - f1, f0, Layout.FramePx, h - 2 * f0, black);                // right

        int ix = Layout.Border, iy = Layout.Border; // inner-area origin
        int gutter = layout.Gutter;

        // Metadata + palette strips, duplicated top and bottom for overlay resilience.
        DrawMetaStrip(px, w, layout, metaModules, ix, iy + gutter);
        DrawPaletteStrip(px, w, layout, palette, ix, iy + gutter + layout.MetaH);
        DrawPaletteStrip(px, w, layout, palette, ix, iy + layout.InnerH - gutter - 2 * layout.MetaH);
        DrawMetaStrip(px, w, layout, metaModules, ix, iy + layout.InnerH - gutter - layout.MetaH);

        // Data grid.
        int dataX = ix + layout.DataLeft, dataY = iy + layout.DataTop;
        int cell = layout.CellPx, bits = layout.BitsPerCell;
        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int v = BitStream.ReadCell(cellBuffer, cellIndex * bits, bits);
                FillRect(px, w, dataX + gx * cell, dataY + gy * cell, cell, cell, palette[v]);
            }
        }

        using var image = Image.LoadPixelData<Rgb24>(px, w, h);
        image.SaveAsPng(outPath);
    }

    private static void DrawMetaStrip(Rgb24[] px, int stride, Layout layout, byte[] metaModules, int ix, int yTop)
    {
        var white = new Rgb24(255, 255, 255);
        var black = new Rgb24(0, 0, 0);
        int gutter = layout.Gutter;
        double moduleW = (layout.InnerW - 2.0 * gutter) / Layout.MetaModuleCount;
        for (int m = 0; m < Layout.MetaModuleCount; m++)
        {
            bool bit = (metaModules[m >> 3] & (0x80 >> (m & 7))) != 0;
            int x0 = ix + gutter + (int)Math.Round(m * moduleW);
            int x1 = ix + gutter + (int)Math.Round((m + 1) * moduleW);
            FillRect(px, stride, x0, yTop, x1 - x0, layout.MetaH, bit ? black : white);
        }
    }

    private static void DrawPaletteStrip(Rgb24[] px, int stride, Layout layout, Rgb24[] palette, int ix, int yTop)
    {
        int gutter = layout.Gutter;
        double blockW = (layout.InnerW - 2.0 * gutter) / palette.Length;
        for (int c = 0; c < palette.Length; c++)
        {
            int x0 = ix + gutter + (int)Math.Round(c * blockW);
            int x1 = ix + gutter + (int)Math.Round((c + 1) * blockW);
            FillRect(px, stride, x0, yTop, x1 - x0, layout.MetaH, palette[c]);
        }
    }

    private static void FillRect(Rgb24[] px, int stride, int x, int y, int rw, int rh, Rgb24 color)
    {
        for (int yy = y; yy < y + rh; yy++)
        {
            int row = yy * stride;
            for (int xx = x; xx < x + rw; xx++)
                px[row + xx] = color;
        }
    }

    /// <summary>
    /// Cheap pre-check before deflating large inputs: compressing a mid-file sample at the
    /// fastest level tells us whether a full pass is worth the CPU (a .zip/.mp4 is not).
    /// </summary>
    internal static bool LooksCompressible(byte[] data)
    {
        const int threshold = 4_000_000, sampleLen = 1_000_000;
        if (data.Length <= threshold)
            return true;
        int offset = data.Length / 2 - sampleLen / 2;
        byte[] sample = data[offset..(offset + sampleLen)];
        return Deflate(sample, CompressionLevel.Fastest).Length < sampleLen * 98L / 100;
    }

    private static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, level))
            ds.Write(data);
        return ms.ToArray();
    }
}
