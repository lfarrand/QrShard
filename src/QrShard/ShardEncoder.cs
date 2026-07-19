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
    public bool CameraMode { get; init; } // add finder patterns so photos (not just screenshots) decode
    public string ImageFormat { get; init; } = ShardImageFormat.Default; // any of ShardImageFormat.Supported
}

internal sealed record EncodeResult(
    int ImageCount, long BytesPerImage, int Width, int Height, List<string> Files,
    int DataImages, int ParityImages, int StripeData, int StripeParity);

internal sealed class ShardEncoder(AppSettings settings) : IShardEncoder
{
    public const long MaxFileBytes = 1_500_000_000; // byte[] limits; also far beyond any sane shard count
    public const int MaxRecoveryPercent = 100;

    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardEncoder() : this(AppSettings.Current)
    {
    }

    public EncodeResult Encode(string filePath, string outDir, EncodeOptions opt, Action<string>? log = null)
    {
        var cfg = settings;
        if (opt.RecoveryPercent is < 0 or > MaxRecoveryPercent)
            throw new ArgumentException($"Recovery percent must be between 0 and {MaxRecoveryPercent}.");
        string format = ShardImageFormat.Normalize(opt.ImageFormat);
        long originalLength = new FileInfo(filePath).Length;
        if (originalLength > MaxFileBytes)
            throw new InvalidOperationException($"Files larger than {MaxFileBytes / 1_000_000:N0} MB are not supported.");
        string fileName = Path.GetFileName(filePath);

        using var payload = OpenPayload(filePath, originalLength, opt.Compress, cfg, out byte flags, out byte[] sha);
        var source = payload.Source;
        long dataLength = source.Length;

        var layout = Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity, opt.CameraMode);
        int headerSize = ShardHeader.Size(fileName);
        long capacityLong = layout.UsableBytes - headerSize;
        if (capacityLong < 1)
            throw new InvalidOperationException("Image capacity is too small for the header; increase resolution or density.");
        int capacity = (int)capacityLong;

        int count = Math.Max(1, (int)((dataLength + capacity - 1) / capacity));
        var (stripeData, stripeParity) = PlanStripes(count, opt.RecoveryPercent);
        int stripes = stripeParity > 0 ? (count + stripeData - 1) / stripeData : 0;
        int parityTotal = stripes * stripeParity;

        ulong fileId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var palette = Palette.Build(opt.BitsPerCell);
        byte[] metaModules = layout.PackMetadata();

        Directory.CreateDirectory(outDir);
        int dataPad = Math.Max(3, count.ToString().Length);
        string extension = ShardImageFormat.Extension(format);

        // Fills a padded (to `capacity`) view of data chunk i — the unit the cross-shard code
        // operates on — into a reusable buffer.
        void FillChunk(int i, byte[] dest)
        {
            long offset = (long)i * capacity;
            int len = (int)Math.Min(capacity, dataLength - offset);
            source.Read(offset, dest.AsSpan(0, len));
            if (len < capacity)
                Array.Clear(dest, len, capacity - len);
        }

        // Compute parity chunks stripe-by-stripe before rendering, reusing one set of chunk
        // buffers across stripes (a 1 GB file would otherwise churn ~1 GB of chunk copies).
        var parityChunks = new byte[parityTotal][];
        if (stripeParity > 0)
        {
            var chunkBuffers = new byte[stripeData][];
            for (int t = 0; t < stripeData; t++)
                chunkBuffers[t] = new byte[capacity];

            for (int g = 0; g < stripes; g++)
            {
                int first = g * stripeData;
                int s = Math.Min(stripeData, count - first);
                for (int t = 0; t < s; t++)
                    FillChunk(first + t, chunkBuffers[t]);
                byte[][] parity = CrossShardFec.Encode(new ArraySegment<byte[]>(chunkBuffers, 0, s), stripeParity, capacity);
                for (int p = 0; p < stripeParity; p++)
                    parityChunks[g * stripeParity + p] = parity[p];
            }
        }

        var files = new string[count + parityTotal];
        int done = 0, totalImages = count + parityTotal;

        // Parallelism is bounded by pixel-buffer memory (one reusable buffer per worker); the
        // budget (default ~2 GB, appsettings.json EncodeMemoryBudgetMB) gives plenty of workers
        // at normal resolutions and few at 16K.
        long pixelBytes = (long)layout.Width * layout.Height * 3;
        long budget = cfg.EncodeMemoryBudgetMB * 1_000_000L;
        int degree = (int)Math.Clamp(budget / Math.Max(1, pixelBytes), 1, Environment.ProcessorCount);
        var po = new ParallelOptions { MaxDegreeOfParallelism = degree };
        var writer = new ShardImageWriter(format, layout, cfg);

        // Data and parity images in ONE parallel loop (no barrier between the phases), with
        // thread-local scratch (pixel canvas + stream/cell byte buffers) so each worker
        // allocates its working set exactly once instead of per image. Data-image payloads are
        // read straight from the (possibly memory-mapped) source into the staging buffer.
        Parallel.For(0, totalImages, po,
            () => new RenderScratch(layout),
            (i, _, scratch) =>
            {
                bool isParity = i >= count;
                int payloadLen;
                string outPath;
                if (!isParity)
                {
                    payloadLen = (int)Math.Min(capacity, dataLength - (long)i * capacity);
                    outPath = Path.Combine(outDir, $"{fileName}.qrs{(i + 1).ToString().PadLeft(dataPad, '0')}of{count.ToString().PadLeft(dataPad, '0')}{extension}");
                }
                else
                {
                    payloadLen = parityChunks[i - count].Length;
                    outPath = Path.Combine(outDir, $"{fileName}.qrs-parity{(i - count + 1).ToString().PadLeft(3, '0')}of{parityTotal.ToString().PadLeft(3, '0')}{extension}");
                }

                // Stage header + payload contiguously: [0..headerSize) header, then the payload.
                int streamLength = headerSize + payloadLen;
                byte[] stream = layout.EccParity > 0 ? scratch.Stream(streamLength) : new byte[streamLength];
                var payloadSpan = stream.AsSpan(headerSize, payloadLen);
                if (isParity)
                    parityChunks[i - count].CopyTo(payloadSpan);
                else
                    source.Read((long)i * capacity, payloadSpan);

                var header = new ShardHeader
                {
                    FileId = fileId,
                    Index = isParity ? i - count : i,
                    Count = count,
                    PayloadLength = payloadLen,
                    PayloadCrc32 = Crc.Crc32(payloadSpan),
                    TotalLength = dataLength,
                    OriginalLength = originalLength,
                    Flags = (byte)(isParity ? flags | ShardHeader.FlagParity : flags),
                    Sha256 = sha,
                    FileName = fileName,
                    StripeData = stripeData,
                    StripeParity = stripeParity,
                };
                header.Serialize().CopyTo(stream, 0);

                RenderShard(layout, palette, metaModules, stream, streamLength, outPath, scratch, writer);
                files[i] = outPath;
                int finished = Interlocked.Increment(ref done);
                log?.Invoke($"  [{finished}/{totalImages}] {Path.GetFileName(outPath)}" +
                            (isParity ? " (parity)" : $" ({payloadLen:N0} bytes)"));
                return scratch;
            },
            _ => { });

        return new EncodeResult(totalImages, capacity, layout.Width, layout.Height, [.. files],
            count, parityTotal, stripeData, stripeParity);
    }

    private sealed class PayloadHandle(IPayloadSource source) : IDisposable
    {
        public IPayloadSource Source => source;

        public void Dispose() => source.Dispose();
    }

    /// <summary>
    /// Chooses how the input is exposed to the encoder:
    ///  - empty file → trivial in-memory source;
    ///  - compressible content (per a mid-file sample for large files) → deflated in memory,
    ///    when that actually wins;
    ///  - everything else → a memory-mapped source, so large incompressible files (zips, media)
    ///    are streamed per-chunk and never materialized as a managed array.
    /// </summary>
    private static PayloadHandle OpenPayload(string filePath, long length, bool compress, AppSettings cfg,
        out byte flags, out byte[] sha)
    {
        flags = 0;
        if (length == 0)
        {
            sha = SHA256.HashData([]);
            return new PayloadHandle(new BytePayloadSource([]));
        }

        var mapped = new MappedPayloadSource(filePath);
        sha = PayloadSource.ComputeSha256(mapped);

        if (compress && LooksCompressible(mapped))
        {
            var original = new byte[length];
            mapped.Read(0, original);
            byte[] compressed = Deflate(original, cfg.PayloadCompressionLevel);
            if (compressed.Length < original.Length)
            {
                mapped.Dispose();
                flags = ShardHeader.FlagCompressed;
                return new PayloadHandle(new BytePayloadSource(compressed));
            }
        }
        return new PayloadHandle(mapped);
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

    /// <summary>Per-worker reusable buffers: the pixel canvas plus the stream/cell staging arrays.</summary>
    private sealed class RenderScratch(Layout layout)
    {
        public readonly Rgb24[] Pixels = new Rgb24[layout.Width * layout.Height];
        private byte[]? _stream;
        private byte[]? _cells;

        public byte[] Stream(int length)
        {
            if (_stream is null || _stream.Length < length)
                _stream = new byte[length];
            return _stream;
        }

        public byte[] Cells(int length)
        {
            if (_cells is null || _cells.Length < length)
                _cells = new byte[length];
            return _cells;
        }
    }

    /// <summary>How rendered pixels are written to disk for the chosen container format.</summary>
    private sealed class ShardImageWriter(string format, Layout layout, AppSettings cfg)
    {
        private readonly bool _fastPng = format == "png";
        private readonly SixLabors.ImageSharp.Formats.IImageEncoder? _encoder = ShardImageFormat.CreateEncoder(format);
        private readonly bool _upFilter = layout.CellPx >= 2;

        // The configured level applies where compression pays off (Up-filtered repeated rows);
        // 1 px noise cells are incompressible by construction, so they always use Fastest.
        private readonly System.IO.Compression.CompressionLevel _pngLevel = layout.CellPx >= 2
            ? cfg.PngCompressionLevel
            : System.IO.Compression.CompressionLevel.Fastest;

        public void Write(string path, Rgb24[] px, int width, int height)
        {
            if (_fastPng)
            {
                FastPng.Write(path, px.AsSpan(0, width * height), width, height, _upFilter, _pngLevel);
                return;
            }
            using var image = Image.LoadPixelData<Rgb24>(
                ShardImageFormat.EncodeConfiguration, px.AsSpan(0, width * height), width, height);
            image.Save(path, _encoder!);
        }
    }

    /// <summary>The stream buffer holds [0..headerSize) header then payload, streamLength bytes total.</summary>
    private static void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream, int streamLength,
        string outPath, RenderScratch scratch, ShardImageWriter writer)
    {
        byte[] cellBuffer;
        if (layout.EccParity > 0)
        {
            // The renderer reads TotalBytes; FEC fills the first CodewordCount*255 of them,
            // so clear the sub-codeword tail to keep pooled-buffer reuse deterministic.
            int cellLength = (int)layout.TotalBytes;
            cellBuffer = scratch.Cells(cellLength);
            Fec.ProtectInto(stream, streamLength, layout.EccParity, layout.CodewordCount, cellBuffer);
            int protectedLength = layout.CodewordCount * Fec.CodewordLength;
            if (protectedLength < cellLength)
                Array.Clear(cellBuffer, protectedLength, cellLength - protectedLength);
        }
        else
        {
            // Without ECC the stream itself is the cell buffer; the caller allocated it exactly
            // sized in this mode, because the renderer treats bytes past the end as zero cells.
            cellBuffer = stream;
        }
        Render(layout, palette, metaModules, cellBuffer, outPath, scratch.Pixels, writer);
    }

    private static void Render(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] cellBuffer, string outPath,
        Rgb24[] px, ShardImageWriter writer)
    {
        int w = layout.Width, h = layout.Height;
        var white = new Rgb24(255, 255, 255);
        var black = new Rgb24(0, 0, 0);
        Array.Fill(px, white);

        // Camera profile shifts the frame + inner content down below the top finder band.
        int oy = layout.ContentTop;
        int contentH = h - 2 * layout.FinderBand;

        // Locator frame ring.
        int f0 = Layout.QuietPx, f1 = Layout.Border; // frame spans [f0, f1) from each content edge
        FillRect(px, w, f0, oy + f0, w - 2 * f0, Layout.FramePx, black);               // top
        FillRect(px, w, f0, oy + contentH - f1, w - 2 * f0, Layout.FramePx, black);    // bottom
        FillRect(px, w, f0, oy + f0, Layout.FramePx, contentH - 2 * f0, black);        // left
        FillRect(px, w, w - f1, oy + f0, Layout.FramePx, contentH - 2 * f0, black);    // right

        if (layout.CameraFinders)
            DrawFinderBands(px, w, h, layout);

        int ix = Layout.Border, iy = oy + Layout.Border; // inner-area origin
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

        writer.Write(outPath, px, w, h);
    }

    /// <summary>
    /// Camera-profile finder bands: four 7-module concentric-square finder patterns at the
    /// image corners (row/column signature 1:1:3:1:1) plus the solid orientation tick
    /// 7 modules right of the top-left finder center. Geometry constants live in Layout and
    /// are shared with the camera rectifier.
    /// </summary>
    private static void DrawFinderBands(Rgb24[] px, int w, int h, Layout layout)
    {
        int m = layout.FinderModule;
        int inset = Layout.FinderCornerInsetModules * m;
        int finder = Layout.FinderModules * m;
        int bottomBandTop = h - layout.FinderBand;

        DrawFinder(px, w, inset, inset, m);                                    // top-left
        DrawFinder(px, w, w - inset - finder, inset, m);                       // top-right
        DrawFinder(px, w, inset, bottomBandTop + inset, m);                    // bottom-left
        DrawFinder(px, w, w - inset - finder, bottomBandTop + inset, m);       // bottom-right

        // Orientation tick: a 3-module solid square centered 7 modules right of the TL finder
        // center (center y = inset + 3.5m). Its mirrored position near the TR finder stays
        // white, which is what disambiguates the four rotations.
        int tickCenterX = inset + finder / 2 + Layout.OrientationTickOffsetModules * m;
        int tickCenterY = inset + finder / 2;
        FillRect(px, w, tickCenterX - (3 * m) / 2, tickCenterY - (3 * m) / 2, 3 * m, 3 * m, new Rgb24(0, 0, 0));
    }

    private static void DrawFinder(Rgb24[] px, int stride, int x, int y, int m)
    {
        var black = new Rgb24(0, 0, 0);
        var white = new Rgb24(255, 255, 255);
        FillRect(px, stride, x, y, 7 * m, 7 * m, black);
        FillRect(px, stride, x + m, y + m, 5 * m, 5 * m, white);
        FillRect(px, stride, x + 2 * m, y + 2 * m, 3 * m, 3 * m, black);
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
    internal static bool LooksCompressible(IPayloadSource source)
    {
        const int threshold = 4_000_000, sampleLen = 1_000_000;
        if (source.Length <= threshold)
            return true;
        var sample = new byte[sampleLen];
        source.Read(source.Length / 2 - sampleLen / 2, sample);
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
