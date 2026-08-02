namespace QrShard;

/// <summary>Public encode settings — a stable subset of the internal options.</summary>
public sealed record QrShardEncodeOptions
{
    /// <summary>Image width in pixels (700-16384).</summary>
    public int Width { get; init; } = 2160;

    /// <summary>Image height in pixels (700-16384).</summary>
    public int Height { get; init; } = 2160;

    /// <summary>Data cell size in pixels (1-64). 3 survives display rescaling; 1 maximizes density.</summary>
    public int CellPx { get; init; } = 3;

    /// <summary>Bits per cell (1-8): 2^n palette colors.</summary>
    public int BitsPerCell { get; init; } = 4;

    /// <summary>Reed-Solomon parity bytes per 255-byte codeword (even, 0-64).</summary>
    public int EccParity { get; init; } = 16;

    /// <summary>Extra parity images as a percent of data images (0-100); whole lost images rebuild.</summary>
    public int RecoveryPercent { get; init; }

    /// <summary>Fountain-coded frames as a percent of data images (0-1000), for video capture.</summary>
    public int FountainPercent { get; init; }

    /// <summary>Add finder patterns so shards decode from photos, not just screenshots.</summary>
    public bool CameraMode { get; init; }

    /// <summary>AES-256-GCM encrypt the payload; decoding requires the same password.</summary>
    public string? Password { get; init; }

    /// <summary>Compress the payload (skipped automatically when incompressible).</summary>
    public bool Compress { get; init; } = true;

    /// <summary>v2 permuted interleave (better vertical-damage spreading; needs ECC).</summary>
    public bool Interleave2 { get; init; }
}

/// <summary>Result of an encode: what was written and the shard geometry.</summary>
public sealed record QrShardEncodeReport(
    int ImageCount, int DataImages, int ParityImages, long BytesPerImage,
    int Width, int Height, IReadOnlyList<string> Files);

/// <summary>One file restored by a decode.</summary>
public sealed record QrShardDecodedFile(string FileName, string OutputPath, long Length);

/// <summary>
/// The public face of the QrShard codec, for embedding in other applications. Wire-format
/// compatible with the `qrshard` CLI in both directions; every decode is SHA-256 verified —
/// a successful return means bit-identical data. Instances are thread-safe and reusable.
/// </summary>
public sealed class QrShardCodec
{
    private readonly ShardEncoder _encoder = new();
    private readonly ShardDecoder _decoder = new();

    /// <summary>
    /// Encodes a file into shard images in <paramref name="outputDirectory"/>.
    /// Throws <see cref="ArgumentException"/> for invalid settings and
    /// <see cref="InvalidOperationException"/> when the input exceeds 1.5 GB or the selected
    /// geometry cannot hold a shard header, and
    /// <see cref="IOException"/>-family exceptions for file-system failures.
    /// </summary>
    public QrShardEncodeReport EncodeFile(string inputPath, string outputDirectory,
        QrShardEncodeOptions? options = null, Action<string>? progress = null)
    {
        var opt = options ?? new QrShardEncodeOptions();
        var result = _encoder.Encode(inputPath, outputDirectory, new EncodeOptions
        {
            Width = opt.Width,
            Height = opt.Height,
            CellPx = opt.CellPx,
            BitsPerCell = opt.BitsPerCell,
            EccParity = opt.EccParity,
            RecoveryPercent = opt.RecoveryPercent,
            FountainPercent = opt.FountainPercent,
            CameraMode = opt.CameraMode,
            Password = opt.Password,
            Compress = opt.Compress,
            Interleave2 = opt.Interleave2,
        }, progress);
        return new QrShardEncodeReport(result.ImageCount, result.DataImages, result.ParityImages,
            result.BytesPerImage, result.Width, result.Height, result.Files);
    }

    /// <summary>
    /// Decodes captured shard images (any order, duplicates fine, damaged captures repaired or
    /// rebuilt from parity) and writes the restored file(s). Output is staged, exact-length and
    /// SHA-256 verified before publication. A single-file result is atomically moved into place.
    /// An archive output directory must be absent or empty and is never merged into an existing
    /// tree; replacing an existing empty directory is not guaranteed to be one atomic operation.
    /// Replacing an existing file preserves only Unix rwx mode, or the Windows DACL and basic
    /// attributes; use a fresh path for extended metadata or hard-link fidelity. Throws
    /// <see cref="QrShardDecodeException"/> with an actionable message when the set cannot be
    /// fully reassembled.
    /// </summary>
    public IReadOnlyList<QrShardDecodedFile> DecodeImages(IEnumerable<string> imagePaths,
        string? outputPath = null, string? password = null, Action<string>? progress = null)
    {
        try
        {
            var restored = _decoder.DecodeFolder(imagePaths, outputPath, progress ?? (_ => { }), password);
            return restored.Select(r => new QrShardDecodedFile(r.FileName, r.OutputPath, r.Length)).ToList();
        }
        catch (ShardDecodeException ex)
        {
            throw new QrShardDecodeException(ex.Message);
        }
    }
}

/// <summary>
/// Per-file completeness within a decode session. <see cref="MissingImages"/> holds a bounded
/// prefix of zero-based data-image ordinals not yet captured (matching the wire index);
/// <see cref="MissingImageCount"/> is always the exact total and
/// <see cref="MissingImagesTruncated"/> says whether more ordinals were omitted. When
/// <see cref="Recoverable"/> is true the file assembles even with some still missing, via parity
/// or fountain frames.
/// </summary>
public sealed record QrShardFileStatus(
    string FileName, int DataPresent, int DataTotal, int ParityPresent,
    IReadOnlyList<int> MissingImages, bool Recoverable)
{
    /// <summary>Exact number of missing data images, including conflicting copies treated as erasures.</summary>
    public int MissingImageCount { get; init; } = MissingImages.Count;

    /// <summary>True when <see cref="MissingImages"/> is only the leading diagnostic sample.</summary>
    public bool MissingImagesTruncated => MissingImages.Count < MissingImageCount;
}

/// <summary>Outcome of adding one image to a session.</summary>
public sealed record QrShardAddResult(bool Accepted, bool WasNew, string? Error);

/// <summary>
/// Incremental decode: feed captures one at a time as they arrive (files or in-memory image
/// bytes), inspect what is still missing, and assemble the moment the set is recoverable —
/// the embedding counterpart to the CLI's --session/--watch. Not thread-safe; drive it from a
/// single consumer. Duplicate captures are harmless (deduplicated by file/part identity). Retained
/// valid shards are bounded by a configurable memory/count budget; a rejected addition reports the
/// limit through <see cref="QrShardAddResult.Error"/> without changing session state.
/// </summary>
public sealed class QrShardDecodeSession
{
    private const int MaximumDecodeMemoryBudgetMB = 1_000_000;
    private readonly string? password;
    private readonly ShardDecoder _decoder = new();
    private readonly ParityReassembler _parity = new();
    private readonly ShardAssembler _assembler = new();
    private readonly DecodeScratch _scratch = new();
    private readonly List<DecodedShard> _shards = [];
    private readonly Dictionary<(ulong, int, bool), DecodedShard?> _seen = [];
    private readonly Dictionary<ulong, ShardHeader> _families = [];
    private readonly long _retainedByteLimit;
    private readonly int _retainedCountLimit;
    private long _retainedBytes;
    internal const int MaxReportedMissingImages = 256;

    /// <summary>
    /// Creates an incremental session with the built-in decode-retention budget (4,000 MB).
    /// </summary>
    public QrShardDecodeSession(string? password = null)
        : this(password, AppSettings.BuiltIn.DecodeMemoryBudgetMB)
    {
    }

    /// <summary>
    /// Creates an incremental session whose retained valid shards are limited to
    /// <paramref name="decodeMemoryBudgetMB"/> decimal megabytes. The same budget also derives a
    /// metadata-aware unique-shard count ceiling. Values from 1 through 1,000,000 are accepted.
    /// </summary>
    public QrShardDecodeSession(string? password, int decodeMemoryBudgetMB)
    {
        if (decodeMemoryBudgetMB is < 1 or > MaximumDecodeMemoryBudgetMB)
            throw new ArgumentOutOfRangeException(nameof(decodeMemoryBudgetMB),
                $"Decode memory budget must be between 1 and {MaximumDecodeMemoryBudgetMB:N0} MB.");
        this.password = password;
        _retainedByteLimit = checked(decodeMemoryBudgetMB * 1_000_000L);
        _retainedCountLimit =
            ShardDecoder.SuccessfulShardRetentionBudget.MaximumInputCountForByteLimit(_retainedByteLimit);
    }

    /// <summary>Decodes an image file and adds its shard to the session.</summary>
    public QrShardAddResult AddImage(string path)
    {
        try
        {
            return Add(_decoder.DecodeImage(path, _scratch));
        }
        catch (ShardDecodeException ex)
        {
            return new QrShardAddResult(false, false, ex.Message);
        }
    }

    /// <summary>Decodes an in-memory encoded image (PNG/BMP/…) and adds its shard.</summary>
    public QrShardAddResult AddImageBytes(ReadOnlySpan<byte> imageBytes, string label = "image")
    {
        try
        {
            return Add(_decoder.DecodeImageBytes(imageBytes, _scratch, label));
        }
        catch (ShardDecodeException ex)
        {
            return new QrShardAddResult(false, false, ex.Message);
        }
    }

    internal QrShardAddResult Add(DecodedShard shard)
    {
        bool newFamily = !_families.TryGetValue(shard.Header.FileId, out ShardHeader? family);
        if (!newFamily && !family!.HasSameFamilyAs(shard.Header))
            return new QrShardAddResult(false, false,
                $"Inconsistent shard family for '{ShardHeader.Display(family.FileName)}': repeated file metadata differs.");

        var key = (shard.Header.FileId, shard.Header.Index, shard.Header.IsParity);
        if (!_seen.TryGetValue(key, out DecodedShard? existing))
        {
            long charge = RetentionCharge(shard);
            if (_seen.Count >= _retainedCountLimit || charge > _retainedByteLimit - _retainedBytes)
                return new QrShardAddResult(false, false,
                    $"Decoded shard retention reached the session budget of " +
                    $"{_retainedByteLimit / 1_000_000:N0} MB or its {_retainedCountLimit:N0}-shard count limit. " +
                    "Split the capture set or create the session with a larger decodeMemoryBudgetMB.");
            if (newFamily)
                _families.Add(shard.Header.FileId, shard.Header);
            _seen.Add(key, shard);
            _shards.Add(shard);
            _retainedBytes += charge;
            return new QrShardAddResult(true, true, null);
        }
        if (existing is not null && existing.Header.PayloadLength == shard.Header.PayloadLength &&
            existing.Header.PayloadCrc32 == shard.Header.PayloadCrc32 &&
            existing.Payload.AsSpan().SequenceEqual(shard.Payload))
            return new QrShardAddResult(true, false, null);

        if (existing is not null)
        {
            _shards.Remove(existing);
            _retainedBytes = checked(_retainedBytes - RetentionCharge(existing) +
                ConflictRetentionCharge(existing));
        }
        _seen[key] = null; // terminal erasure: never let a later alternative become first-wins
        string kind = shard.Header.IsParity ? "parity" : "data";
        return new QrShardAddResult(false, false,
            $"Conflicting CRC-valid {kind} copies for ordinal {shard.Header.Index}; treating that ordinal as missing.");
    }

    private static long RetentionCharge(DecodedShard shard) => checked(
        2L * ShardHeader.Size(shard.Header.FileName) + 2L * shard.SourceFile.Length +
        shard.Payload.Length + ShardDecoder.SuccessfulShardRetentionBudget.PerShardOverheadBytes);

    private static long ConflictRetentionCharge(DecodedShard shard) => checked(
        2L * ShardHeader.Size(shard.Header.FileName) + 2L * shard.SourceFile.Length +
        ShardDecoder.SuccessfulShardRetentionBudget.PerShardOverheadBytes);

    /// <summary>True when every file in the session can be fully reassembled.</summary>
    public bool IsComplete => _families.Count > 0 &&
        _shards.Select(s => s.Header.FileId).Distinct().Count() == _families.Count &&
        _parity.IsSetComplete(_shards);

    /// <summary>Per-file progress: what is present, what is missing, and whether parity covers it.</summary>
    public IReadOnlyList<QrShardFileStatus> Status()
    {
        var result = new List<QrShardFileStatus>();
        var byFile = _shards.ToLookup(s => s.Header.FileId);
        foreach ((ulong fileId, ShardHeader first) in _families)
        {
            var group = byFile[fileId].ToList();
            var have = group.Where(s => !s.Header.IsParity).Select(s => s.Header.Index).ToHashSet();
            int missingCount = first.Count - have.Count;
            var missing = new List<int>(Math.Min(MaxReportedMissingImages, missingCount));
            for (int i = 0; i < first.Count && missing.Count < MaxReportedMissingImages; i++)
                if (!have.Contains(i))
                    missing.Add(i);
            result.Add(new QrShardFileStatus(
                first.FileName, have.Count, first.Count, group.Count(s => s.Header.IsParity),
                missing, group.Count > 0 && _parity.IsSetComplete(group))
                { MissingImageCount = missingCount });
        }
        return result;
    }

    /// <summary>
    /// Assembles the collected shards into the restored file(s). Throws
    /// <see cref="QrShardDecodeException"/> if the set is not yet complete (check
    /// <see cref="IsComplete"/> first) or if verification fails. Output is staged and published
    /// only after exact-length and SHA-256 verification. A single-file result is atomically moved
    /// into place; an archive destination must be absent or empty and is never merged, but replacing
    /// an existing empty directory is not guaranteed to be one atomic operation. Replacing an
    /// existing file preserves only Unix rwx mode, or the Windows DACL and basic attributes.
    /// </summary>
    public IReadOnlyList<QrShardDecodedFile> Assemble(string? outputPath = null, Action<string>? progress = null)
    {
        try
        {
            if (!IsComplete)
                throw new ShardDecodeException("The decode session is incomplete; capture the missing images before assembling.");
            var restored = _assembler.Assemble(_shards, outputPath, progress ?? (_ => { }), password);
            return restored.Select(r => new QrShardDecodedFile(r.FileName, r.OutputPath, r.Length)).ToList();
        }
        catch (ShardDecodeException ex)
        {
            throw new QrShardDecodeException(ex.Message);
        }
    }
}

/// <summary>A decode failure; the message is user-facing and actionable.</summary>
public sealed class QrShardDecodeException(string message) : Exception(message);
