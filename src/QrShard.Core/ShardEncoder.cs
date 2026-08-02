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

    private readonly record struct Geometry(Layout Layout, int HeaderSize, int Capacity,
        int Count, int StripeData, int StripeParity, int Stripes, int ParityTotal, int TotalImages);

    private readonly record struct InputSnapshot(long Length, long LastWriteUtcTicks, long CreationUtcTicks);

    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardEncoder() : this(AppSettings.BuiltIn, new PayloadPreparer(), new StripePlanner(),
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
        string inputPath = Path.GetFullPath(filePath);
        InputSnapshot input = CaptureInput(inputPath);
        string fileName = Path.GetFileName(inputPath);
        byte semanticFlags = (byte)((opt.IsArchive ? ShardHeader.FlagArchive : 0) |
            (fountain ? ShardHeader.FlagFountain : 0));
        using var payload = payloadPreparer.Open(inputPath, input.Length, opt.Compress, opt.Password, settings,
            semanticFlags, out _, out _);
        EnsurePreparedLengthSupported(payload.Source.Length);
        EnsureInputMetadataUnchanged(inputPath, input);
        var g = ComputeGeometry(opt, fileName, payload.Source.Length, fountain);
        return new EncodePlan(g.TotalImages, g.Count, g.ParityTotal, g.Capacity,
            g.Layout.Width, g.Layout.Height, g.StripeData, g.StripeParity, format);
    }

    private static bool ValidateEncodeOptions(EncodeOptions opt)
    {
        if (opt.Password is { Length: 0 })
            throw new ArgumentException("Password must not be empty; use null for plaintext output.");
        if (opt.RecoveryPercent is < 0 or > MaxRecoveryPercent)
            throw new ArgumentException($"Recovery percent must be between 0 and {MaxRecoveryPercent}.");
        if (opt.FountainPercent is < 0 or > MaxFountainPercent)
            throw new ArgumentException($"Fountain percent must be between 0 and {MaxFountainPercent}.");
        if (opt.FountainPercent > 0 && opt.RecoveryPercent > 0)
            throw new ArgumentException("Use either recovery parity or fountain coding, not both.");
        return opt.FountainPercent > 0;
    }

    private static InputSnapshot CaptureInput(string filePath)
    {
        var info = new FileInfo(filePath);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("Input file was not found.", filePath);
        long len = info.Length;
        if (len > MaxFileBytes)
            throw new InvalidOperationException($"Files larger than {MaxFileBytes / 1_000_000:N0} MB are not supported.");
        return new InputSnapshot(len, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    internal static void EnsurePreparedLengthSupported(long length)
    {
        if (length < 0 || length > MaxFileBytes)
            throw new InvalidOperationException(
                $"The prepared payload, including compression/encryption overhead, exceeds the " +
                $"{MaxFileBytes / 1_000_000:N0} MB protocol limit.");
    }

    private static void EnsureInputMetadataUnchanged(string filePath, InputSnapshot expected)
    {
        InputSnapshot current = CaptureInput(filePath);
        if (current != expected)
            throw new IOException("The input file changed while it was being encoded. No shard generation was published.");
    }

    private static void EnsureInputUnchanged(string filePath, InputSnapshot expected, byte[] expectedSha)
    {
        EnsureInputMetadataUnchanged(filePath, expected);
        byte[] currentSha;
        using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   4 * 1024 * 1024, FileOptions.SequentialScan))
            currentSha = SHA256.HashData(input);
        EnsureInputMetadataUnchanged(filePath, expected);
        if (!CryptographicOperations.FixedTimeEquals(currentSha, expectedSha))
            throw new IOException("The input file changed while it was being encoded. No shard generation was published.");
    }

    /// <summary>Layout + image/stripe counts for a given post-preparation payload size. Shared by
    /// Encode and Plan so the dry-run count can never drift from the real one.</summary>
    private Geometry
        ComputeGeometry(EncodeOptions opt, string fileName, long dataLength, bool fountain)
    {
        EnsurePreparedLengthSupported(dataLength);
        var layout = Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity, opt.CameraMode,
            opt.Interleave2);
        int headerSize = ShardHeader.Size(fileName);
        long capacityLong = checked(layout.UsableBytes - (long)headerSize);
        if (capacityLong < 1)
            throw new InvalidOperationException("Image capacity is too small for the header; increase resolution or density.");
        if (capacityLong > int.MaxValue)
            throw new InvalidOperationException("Image capacity exceeds the supported per-shard limit.");
        int capacity = checked((int)capacityLong);
        long countLong = dataLength == 0
            ? 1
            : checked(dataLength / capacityLong + (dataLength % capacityLong == 0 ? 0 : 1));
        if (countLong > ShardHeader.MaxImages)
            throw new InvalidOperationException(
                $"This layout needs {countLong:N0} data images, above the protocol limit of " +
                $"{ShardHeader.MaxImages:N0}. Increase capacity per image or split the input.");
        int count = checked((int)countLong);
        var (stripeData, stripeParity) = fountain
            ? stripePlanner.PlanFountain(count, opt.FountainPercent)
            : stripePlanner.PlanStripes(count, opt.RecoveryPercent);
        if (stripeData < 0 || stripeParity < 0 || (stripeParity == 0) != (stripeData == 0))
            throw new InvalidOperationException("The recovery planner returned invalid stripe geometry.");

        long stripesLong = 0, parityTotalLong = 0;
        if (stripeParity > 0)
        {
            if (stripeData > CrossShardFec.MaxShardsPerStripe ||
                (!fountain && checked((long)stripeData + stripeParity) > CrossShardFec.MaxShardsPerStripe))
                throw new InvalidOperationException("The recovery planner returned unsupported stripe geometry.");
            stripesLong = checked(countLong / stripeData + (countLong % stripeData == 0 ? 0 : 1));
            parityTotalLong = checked(stripesLong * stripeParity);
            if (parityTotalLong > ShardHeader.MaxParityOrdinals)
                throw new InvalidOperationException(
                    $"This layout needs {parityTotalLong:N0} recovery images, above the protocol limit of " +
                    $"{ShardHeader.MaxParityOrdinals:N0}.");
        }

        long totalLong = checked(countLong + parityTotalLong);
        if (totalLong > int.MaxValue)
            throw new InvalidOperationException("The total shard image count exceeds the supported in-memory result limit.");

        return new Geometry(layout, headerSize, capacity, count, stripeData, stripeParity,
            checked((int)stripesLong), checked((int)parityTotalLong), checked((int)totalLong));
    }

    public EncodeResult Encode(string filePath, string outDir, EncodeOptions opt, Action<string>? log = null)
    {
        bool fountain = ValidateEncodeOptions(opt);
        string format = formats.Normalize(opt.ImageFormat);
        string inputPath = Path.GetFullPath(filePath);
        InputSnapshot input = CaptureInput(inputPath);
        long originalLength = input.Length;
        string fileName = Path.GetFileName(inputPath);

        byte semanticFlags = (byte)((opt.IsArchive ? ShardHeader.FlagArchive : 0) |
            (fountain ? ShardHeader.FlagFountain : 0));
        using var payload = payloadPreparer.Open(inputPath, originalLength, opt.Compress, opt.Password, settings,
            semanticFlags, out byte flags, out byte[] sha);
        var source = payload.Source;
        long dataLength = source.Length;
        EnsurePreparedLengthSupported(dataLength);
        EnsureInputMetadataUnchanged(inputPath, input);

        Geometry geometry = ComputeGeometry(opt, fileName, dataLength, fountain);
        var (layout, headerSize, capacity, count, stripeData, stripeParity, stripes, parityTotal, totalImages) = geometry;

        // Bound fixed resident payload/FEC storage before allocating parity. Compression and
        // password encryption can turn the source into one retained managed array, while parity
        // retains one capacity-sized chunk per recovery image. The old worker-only calculation
        // ignored both and could exceed the configured budget before rendering even started.
        long maxStreamBytes = checked(headerSize + (long)capacity);
        long renderWorkerBytes = EstimateRenderWorkerBytes(
            layout, maxStreamBytes, imageWriterCopiesPixels: format != "png");
        long permutationBytes = EstimateSharedInterleaveBytes(layout);
        long parityBytes = checked((long)parityTotal * capacity);
        long stripeScratchBytes = stripeParity > 0 ? checked((long)stripeData * capacity) : 0;
        long parityArrayOverhead = checked((long)parityTotal * (IntPtr.Size + 24));
        long resultPathBytes = EstimateResultPathBytes(outDir, fileName, format, totalImages);
        long resultReferenceBytes = checked((long)totalImages * IntPtr.Size * 2); // render array + returned List backing array
        long fixedResidentBytes = checked(source.ResidentBytes + parityBytes + stripeScratchBytes +
            parityArrayOverhead + permutationBytes +
            (long)stripeData * IntPtr.Size + resultReferenceBytes + resultPathBytes);
        long budget = checked(settings.EncodeMemoryBudgetMB * 1_000_000L);
        if (fixedResidentBytes > budget - renderWorkerBytes)
            throw new InvalidOperationException(
                $"This encode plans ~{fixedResidentBytes / 1_000_000:N0} MB of fixed payload/recovery/output state plus " +
                $"at least one ~{renderWorkerBytes / 1_000_000:N0} MB render working set, above " +
                $"EncodeMemoryBudgetMB={settings.EncodeMemoryBudgetMB:N0}. Lower recovery, use --no-compress, " +
                "split the input, reduce resolution, or raise the budget deliberately.");

        ulong fileId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var palette = paletteBuilder.Build(opt.BitsPerCell);
        byte[] metaModules = layout.PackMetadata();
        using var output = new OutputTransaction(outDir);
        int[]? sharedPermutation = renderer.PrepareInterleave(layout);
        int dataPad = Math.Max(3, count.ToString().Length);
        string extension = formats.Extension(format);

        // Fills a padded (to `capacity`) view of data chunk i — the unit the cross-shard code
        // operates on — into a reusable buffer.
        void FillChunk(int i, byte[] dest)
        {
            long offset = checked((long)i * capacity);
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
                int first = checked((int)((long)g * stripeData));
                int s = Math.Min(stripeData, count - first);
                for (int t = 0; t < s; t++)
                    FillChunk(first + t, chunkBuffers[t]);
                if (fountain)
                {
                    // Fountain ordinals are round-robin across stripes (o -> stripe o % stripes)
                    // so a cycling slideshow spreads every stripe's coded frames evenly.
                    for (int seq = 0; seq < stripeParity; seq++)
                        parityChunks[checked((int)((long)seq * stripes + g))] =
                            fountainFec.EncodeFrame(new ArraySegment<byte[]>(chunkBuffers, 0, s), fileId, g, seq, capacity);
                }
                else
                {
                    byte[][] parity = crossShardFec.Encode(new ArraySegment<byte[]>(chunkBuffers, 0, s), stripeParity, capacity);
                    for (int p = 0; p < stripeParity; p++)
                        parityChunks[checked((int)((long)g * stripeParity + p))] = parity[p];
                }
            }
        }

        var files = new string[totalImages];
        int done = 0;

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
                string relativeName;
                if (!isParity)
                {
                    payloadLen = (int)Math.Min(capacity, dataLength - checked((long)i * capacity));
                    relativeName = $"{fileName}.qrs{(i + 1).ToString().PadLeft(dataPad, '0')}of{count.ToString().PadLeft(dataPad, '0')}{extension}";
                }
                else
                {
                    payloadLen = parityChunks[i - count].Length;
                    relativeName = $"{fileName}.qrs-parity{(i - count + 1).ToString().PadLeft(3, '0')}of{parityTotal.ToString().PadLeft(3, '0')}{extension}";
                }
                string stagedPath = Path.Combine(output.StagingDirectory, relativeName);
                string finalPath = Path.Combine(output.TargetDirectory, relativeName);

                // Stage header + payload contiguously: [0..headerSize) header, then the payload.
                int streamLength = checked(headerSize + payloadLen);
                int stagedLength = layout.EccParity > 0
                    ? streamLength
                    : checked((int)layout.TotalBytes);
                byte[] stream = scratch.Stream(stagedLength);
                var payloadSpan = stream.AsSpan(headerSize, payloadLen);
                if (isParity)
                    parityChunks[i - count].CopyTo(payloadSpan);
                else
                    source.Read(checked((long)i * capacity), payloadSpan);
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

                renderer.RenderShard(layout, palette, metaModules, stream, streamLength,
                    stagedPath, scratch, writer, sharedPermutation);
                files[i] = finalPath;
                int finished = Interlocked.Increment(ref done);
                // Serialize the progress callback: it runs on every parallel worker, and a caller
                // may pass a delegate that is not thread-safe (a StringBuilder/StringWriter sink,
                // a List.Add). The real CLI writes to a synchronized Console.Out, but library
                // consumers of the progress action must be protected too. Cost is negligible.
                if (log is not null)
                    lock (logLock)
                        log($"  [{finished}/{totalImages}] {ShardHeader.Display(Path.GetFileName(finalPath))}" +
                            (isParity ? " (parity)" : $" ({payloadLen:N0} bytes)"));
            }
        });

        // Rendering reads a memory map in parallel. A sender editing/replacing the source during
        // that interval must never receive a successful result for shards that no longer match
        // the identity hash in their headers. Recheck metadata around a final streaming SHA pass
        // before the staged generation becomes visible.
        EnsureInputUnchanged(inputPath, input, sha);
        // Materialize every allocation needed by the success result before the commit boundary.
        // With millions of images even the List's reference array is material: an OOM here after
        // Publish would expose a complete generation while reporting failure to the caller.
        var result = new EncodeResult(totalImages, capacity, layout.Width, layout.Height,
            new List<string>(files), count, parityTotal, stripeData, stripeParity);
        output.Publish();
        return result;
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

    /// <summary>The v2 permutation is immutable and shared by every worker in one encode.</summary>
    internal static long EstimateSharedInterleaveBytes(Layout layout) => layout.Interleave2
        ? checked((long)layout.CodewordCount * Fec.CodewordLength * sizeof(int))
        : 0;

    private static long EstimateResultPathBytes(string outDir, string fileName, string format, int totalImages)
    {
        // A List result retains one full path string per image. Include a conservative object and
        // filename suffix allowance so extreme sparse plans fail the memory admission check before
        // allocating millions of strings/references.
        long chars = checked((long)Path.GetFullPath(outDir).Length + fileName.Length + format.Length + 96);
        long bytesPerPath = checked(24 + chars * sizeof(char));
        return checked(bytesPerPath * totalImages);
    }

    /// <summary>
    /// Renders a complete generation into a private sibling and publishes it with one directory
    /// rename. A caller-supplied empty destination is left untouched on failure; a non-empty one
    /// is refused so shards from different generations can never be mixed silently.
    /// </summary>
    internal sealed class OutputTransaction : IDisposable
    {
        private bool published;
        private readonly bool targetExisted;

        internal OutputTransaction(string outDir)
        {
            TargetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
            string? parent = Path.GetDirectoryName(TargetDirectory);
            if (parent is null || TargetDirectory == Path.GetPathRoot(TargetDirectory))
                throw new InvalidOperationException("Refusing to encode directly into a filesystem root.");
            if (File.Exists(TargetDirectory))
                throw new IOException($"Output destination '{ShardHeader.Display(outDir)}' is a file.");
            targetExisted = Directory.Exists(TargetDirectory);
            if (targetExisted)
            {
                if ((File.GetAttributes(TargetDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Output destination '{ShardHeader.Display(outDir)}' cannot be a symbolic link or reparse point.");
                EnsureEmpty(TargetDirectory, outDir);
            }

            Directory.CreateDirectory(parent);
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string candidate = Path.Combine(parent, $".qrshard-encode-{Guid.NewGuid():N}.tmp");
                try
                {
                    ShardAssembler.CreatePrivateDirectoryExclusive(candidate);
                    StagingDirectory = candidate;
                    return;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    // Exclusive create proved a collision; try another unpredictable sibling.
                }
            }
            throw new IOException("Could not create a private output staging directory after 32 attempts.");
        }

        internal string TargetDirectory { get; }
        internal string StagingDirectory { get; } = null!;

        internal void Publish()
        {
            if (published)
                throw new InvalidOperationException("The output generation has already been published.");

            if (File.Exists(TargetDirectory))
                throw new IOException("The output destination changed to a file before publication.");
            if (Directory.Exists(TargetDirectory))
            {
                if (!targetExisted)
                    throw new IOException("The output destination was created by another writer before publication.");
                EnsureEmpty(TargetDirectory, TargetDirectory);
                Directory.Delete(TargetDirectory);
            }
            else if (targetExisted)
            {
                throw new IOException("The caller-supplied output directory disappeared before publication.");
            }

            try
            {
                Directory.Move(StagingDirectory, TargetDirectory);
                published = true;
            }
            catch
            {
                // If the caller supplied an empty directory, publication must not turn a failed
                // encode into a missing destination. Recreate only when no competing object won.
                if (targetExisted && !Directory.Exists(TargetDirectory) && !File.Exists(TargetDirectory))
                    Directory.CreateDirectory(TargetDirectory);
                throw;
            }
        }

        private static void EnsureEmpty(string path, string displayPath)
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new IOException(
                    $"Output destination '{displayPath}' is not empty. Choose a new/empty directory for this generation.");
        }

        public void Dispose()
        {
            if (published || !Directory.Exists(StagingDirectory))
                return;
            try
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
            catch
            {
                // Best effort: the private, uniquely named incomplete generation is never returned
                // or published, and a cleanup failure must not hide the original encode error.
            }
        }
    }
}
