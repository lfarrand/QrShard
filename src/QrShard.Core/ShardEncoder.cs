using System.Security.Cryptography;

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
    public string? Password { get; init; } // AES-256-GCM encrypt the payload (null = plaintext)
    public bool IsArchive { get; init; } // payload is a tar of a folder; decode extracts it
    public int FountainPercent { get; init; } // fountain-coded frames (% of data, video mode); 0 = off
    public bool Interleave2 { get; init; } // v2 permutation (metadata v3, or v4 flag; needs ECC)
}

internal sealed record EncodeResult(
    int ImageCount, long BytesPerImage, int Width, int Height, List<string> Files,
    int DataImages, int ParityImages, int StripeData, int StripeParity);

/// <summary>What an encode WOULD produce, computed without rendering — the dry-run preview.</summary>
internal sealed record EncodePlan(
    int ImageCount, int DataImages, int ParityImages, long BytesPerImage,
    int Width, int Height, int StripeData, int StripeParity, string Format);

/// <summary>
/// The encode orchestrator: sizes the layout, plans stripes, computes cross-shard parity, and
/// runs the parallel per-image render loop. Payload preparation, stripe planning, and
/// rasterization live in their injected collaborators.
/// </summary>
internal sealed class ShardEncoder(
    AppSettings settings, IPayloadPreparer payloadPreparer, IStripePlanner stripePlanner,
    IShardRenderer renderer, CrossShardFec crossShardFec, FountainFec fountainFec, Crc crc,
    Palette paletteBuilder, ShardImageFormat formats) : IShardEncoder
{
    public const long MaxFileBytes = 1_500_000_000; // byte[] limits; also far beyond any sane shard count
    public const int MaxRecoveryPercent = 100;
    public const int MaxFountainPercent = 1000;

    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardEncoder() : this(AppSettings.Current, new PayloadPreparer(), new StripePlanner(),
        new ShardRenderer(), new CrossShardFec(), new FountainFec(), new Crc(), new Palette(), new ShardImageFormat())
    {
    }

    /// <summary>Computes what an encode would produce (image counts, geometry) WITHOUT rendering —
    /// backs `encode --dry-run`. Opens the payload so the count reflects real post-compression
    /// size, which is cheap relative to rendering.</summary>
    public EncodePlan Plan(string filePath, EncodeOptions opt)
    {
        bool fountain = ValidateEncodeOptions(opt);
        string format = formats.Normalize(opt.ImageFormat);
        long originalLength = FileSizeChecked(filePath);
        string fileName = Path.GetFileName(filePath);
        using var payload = payloadPreparer.Open(filePath, originalLength, opt.Compress, opt.Password, settings, out _, out _);
        var g = ComputeGeometry(opt, fileName, payload.Source.Length, fountain);
        return new EncodePlan(g.Count + g.ParityTotal, g.Count, g.ParityTotal, g.Capacity,
            g.Layout.Width, g.Layout.Height, g.StripeData, g.StripeParity, format);
    }

    private static bool ValidateEncodeOptions(EncodeOptions opt)
    {
        if (opt.RecoveryPercent is < 0 or > MaxRecoveryPercent)
            throw new ArgumentException($"Recovery percent must be between 0 and {MaxRecoveryPercent}.");
        if (opt.FountainPercent is < 0 or > MaxFountainPercent)
            throw new ArgumentException($"Fountain percent must be between 0 and {MaxFountainPercent}.");
        if (opt.FountainPercent > 0 && opt.RecoveryPercent > 0)
            throw new ArgumentException("Use either recovery parity or fountain coding, not both.");
        return opt.FountainPercent > 0;
    }

    private static long FileSizeChecked(string filePath)
    {
        long len = new FileInfo(filePath).Length;
        if (len > MaxFileBytes)
            throw new InvalidOperationException($"Files larger than {MaxFileBytes / 1_000_000:N0} MB are not supported.");
        return len;
    }

    /// <summary>Layout + image/stripe counts for a given post-preparation payload size. Shared by
    /// Encode and Plan so the dry-run count can never drift from the real one.</summary>
    private (Layout Layout, int HeaderSize, int Capacity, int Count, int StripeData, int StripeParity, int Stripes, int ParityTotal)
        ComputeGeometry(EncodeOptions opt, string fileName, long dataLength, bool fountain)
    {
        var layout = Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity, opt.CameraMode,
            opt.Interleave2);
        int headerSize = ShardHeader.Size(fileName);
        long capacityLong = layout.UsableBytes - headerSize;
        if (capacityLong < 1)
            throw new InvalidOperationException("Image capacity is too small for the header; increase resolution or density.");
        int capacity = (int)capacityLong;
        int count = Math.Max(1, (int)((dataLength + capacity - 1) / capacity));
        var (stripeData, stripeParity) = fountain
            ? stripePlanner.PlanFountain(count, opt.FountainPercent)
            : stripePlanner.PlanStripes(count, opt.RecoveryPercent);
        int stripes = stripeParity > 0 ? (count + stripeData - 1) / stripeData : 0;
        return (layout, headerSize, capacity, count, stripeData, stripeParity, stripes, stripes * stripeParity);
    }

    public EncodeResult Encode(string filePath, string outDir, EncodeOptions opt, Action<string>? log = null)
    {
        bool fountain = ValidateEncodeOptions(opt);
        string format = formats.Normalize(opt.ImageFormat);
        long originalLength = FileSizeChecked(filePath);
        string fileName = Path.GetFileName(filePath);

        using var payload = payloadPreparer.Open(filePath, originalLength, opt.Compress, opt.Password, settings,
            out byte flags, out byte[] sha);
        if (opt.IsArchive)
            flags |= ShardHeader.FlagArchive;
        if (fountain)
            flags |= ShardHeader.FlagFountain;
        var source = payload.Source;
        long dataLength = source.Length;

        var (layout, headerSize, capacity, count, stripeData, stripeParity, stripes, parityTotal) =
            ComputeGeometry(opt, fileName, dataLength, fountain);

        // Bound fixed resident payload/FEC storage before allocating parity. Compression and
        // password encryption can turn the source into one retained managed array, while parity
        // retains one capacity-sized chunk per recovery image. The old worker-only calculation
        // ignored both and could exceed the configured budget before rendering even started.
        long maxStreamBytes = checked(headerSize + (long)capacity);
        long renderWorkerBytes = EstimateRenderWorkerBytes(
            layout, maxStreamBytes, imageWriterCopiesPixels: format != "png");
        long parityBytes = checked((long)parityTotal * capacity);
        long stripeScratchBytes = stripeParity > 0 ? checked((long)stripeData * capacity) : 0;
        long fixedResidentBytes = checked(source.ResidentBytes + parityBytes + stripeScratchBytes +
            (long)parityTotal * IntPtr.Size);
        long budget = checked(settings.EncodeMemoryBudgetMB * 1_000_000L);
        if (fixedResidentBytes > budget - renderWorkerBytes)
            throw new InvalidOperationException(
                $"This encode plans ~{fixedResidentBytes / 1_000_000:N0} MB of resident payload/parity data plus " +
                $"at least one ~{renderWorkerBytes / 1_000_000:N0} MB render working set, above " +
                $"EncodeMemoryBudgetMB={settings.EncodeMemoryBudgetMB:N0}. Lower recovery, use --no-compress, " +
                "split the input, reduce resolution, or raise the budget deliberately.");

        ulong fileId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var palette = paletteBuilder.Build(opt.BitsPerCell);
        byte[] metaModules = layout.PackMetadata();

        Directory.CreateDirectory(outDir);
        int dataPad = Math.Max(3, count.ToString().Length);
        string extension = formats.Extension(format);

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
                if (fountain)
                {
                    // Fountain ordinals are round-robin across stripes (o -> stripe o % stripes)
                    // so a cycling slideshow spreads every stripe's coded frames evenly.
                    for (int seq = 0; seq < stripeParity; seq++)
                        parityChunks[seq * stripes + g] =
                            fountainFec.EncodeFrame(new ArraySegment<byte[]>(chunkBuffers, 0, s), fileId, g, seq, capacity);
                }
                else
                {
                    byte[][] parity = crossShardFec.Encode(new ArraySegment<byte[]>(chunkBuffers, 0, s), stripeParity, capacity);
                    for (int p = 0; p < stripeParity; p++)
                        parityChunks[g * stripeParity + p] = parity[p];
                }
            }
        }

        var files = new string[count + parityTotal];
        int done = 0, totalImages = count + parityTotal;

        // Parallelism is bounded by the full known render working set: RGB canvas, stream, FEC
        // cells, optional interleave scatter, and the ImageSharp pixel copy for non-PNG formats.
        // Codec-internal compression storage remains format/input dependent, so this is a
        // conservative planner for buffers QrShard controls rather than a process-RSS ceiling.
        long workerBudget = budget - fixedResidentBytes;
        int degree = (int)Math.Clamp(workerBudget / Math.Max(1, renderWorkerBytes), 1,
            Math.Min(Environment.ProcessorCount, totalImages));
        var po = new ParallelOptions { MaxDegreeOfParallelism = degree };
        var logLock = new object(); // serializes the per-image progress callback across workers

        // Run exactly `degree` cursor workers. Parallel.For's thread-local overload can create
        // replacement locals as scheduler tasks turn over; explicit workers make the planned
        // number of retained RenderScratch instances exact. Data and parity still share one
        // queue, with no phase barrier.
        int cursor = -1;
        Parallel.For(0, degree, po, _ =>
        {
            var scratch = new RenderScratch(layout);
            var writer = renderer.CreateWriter(format, layout, settings);
            while (true)
            {
                int i = Interlocked.Increment(ref cursor);
                if (i >= totalImages)
                    break;
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
                int stagedLength = layout.EccParity > 0
                    ? streamLength
                    : checked((int)layout.TotalBytes);
                byte[] stream = scratch.Stream(stagedLength);
                var payloadSpan = stream.AsSpan(headerSize, payloadLen);
                if (isParity)
                    parityChunks[i - count].CopyTo(payloadSpan);
                else
                    source.Read((long)i * capacity, payloadSpan);
                if (streamLength < stagedLength)
                    Array.Clear(stream, streamLength, stagedLength - streamLength);

                var header = new ShardHeader
                {
                    FileId = fileId,
                    Index = isParity ? i - count : i,
                    Count = count,
                    PayloadLength = payloadLen,
                    PayloadCrc32 = crc.Crc32(payloadSpan),
                    TotalLength = dataLength,
                    OriginalLength = originalLength,
                    Flags = (byte)(isParity ? flags | ShardHeader.FlagParity : flags),
                    Sha256 = sha,
                    FileName = fileName,
                    StripeData = stripeData,
                    StripeParity = stripeParity,
                };
                header.Serialize().CopyTo(stream, 0);

                renderer.RenderShard(layout, palette, metaModules, stream, streamLength, outPath, scratch, writer);
                files[i] = outPath;
                int finished = Interlocked.Increment(ref done);
                // Serialize the progress callback: it runs on every parallel worker, and a caller
                // may pass a delegate that is not thread-safe (a StringBuilder/StringWriter sink,
                // a List.Add). The real CLI writes to a synchronized Console.Out, but library
                // consumers of the progress action must be protected too. Cost is negligible.
                if (log is not null)
                    lock (logLock)
                        log($"  [{finished}/{totalImages}] {Path.GetFileName(outPath)}" +
                            (isParity ? " (parity)" : $" ({payloadLen:N0} bytes)"));
            }
        });

        return new EncodeResult(totalImages, capacity, layout.Width, layout.Height, [.. files],
            count, parityTotal, stripeData, stripeParity);
    }

    /// <summary>
    /// Known peak bytes retained by one render worker. Count all buffers conservatively even when
    /// ECC is disabled so an option change cannot make the admission calculation optimistic.
    /// Non-PNG ImageSharp writers materialize their own pixel image in addition to our canvas.
    /// </summary>
    internal static long EstimateRenderWorkerBytes(Layout layout, long maxStreamBytes,
        bool imageWriterCopiesPixels)
    {
        long pixels = checked((long)layout.Width * layout.Height * 3);
        long cells = layout.TotalBytes;
        return checked(pixels + maxStreamBytes + cells +
            (layout.Interleave2 ? cells : 0) +
            (imageWriterCopiesPixels ? pixels : 0));
    }
}
