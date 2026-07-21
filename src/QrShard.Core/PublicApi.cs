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
    /// rebuilt from parity) and writes the restored file(s). Throws
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
/// Per-file completeness within a decode session. <see cref="MissingImages"/> holds the
/// zero-based ordinals of the data images not yet captured (matching the wire index); when
/// <see cref="Recoverable"/> is true the file assembles even with some still missing, via
/// parity or fountain frames.
/// </summary>
public sealed record QrShardFileStatus(
    string FileName, int DataPresent, int DataTotal, int ParityPresent,
    IReadOnlyList<int> MissingImages, bool Recoverable);

/// <summary>Outcome of adding one image to a session.</summary>
public sealed record QrShardAddResult(bool Accepted, bool WasNew, string? Error);

/// <summary>
/// Incremental decode: feed captures one at a time as they arrive (files or in-memory image
/// bytes), inspect what is still missing, and assemble the moment the set is recoverable —
/// the embedding counterpart to the CLI's --session/--watch. Not thread-safe; drive it from a
/// single consumer. Duplicate captures are harmless (deduplicated by file/part identity).
/// </summary>
public sealed class QrShardDecodeSession(string? password = null)
{
    private readonly ShardDecoder _decoder = new();
    private readonly ParityReassembler _parity = new();
    private readonly ShardAssembler _assembler = new();
    private readonly DecodeScratch _scratch = new();
    private readonly List<DecodedShard> _shards = [];
    private readonly HashSet<(ulong, int, bool)> _seen = [];

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

    private QrShardAddResult Add(DecodedShard shard)
    {
        bool isNew = _seen.Add((shard.Header.FileId, shard.Header.Index, shard.Header.IsParity));
        if (isNew)
            _shards.Add(shard);
        return new QrShardAddResult(true, isNew, null);
    }

    /// <summary>True when every file in the session can be fully reassembled.</summary>
    public bool IsComplete => _shards.Count > 0 && _parity.IsSetComplete(_shards);

    /// <summary>Per-file progress: what is present, what is missing, and whether parity covers it.</summary>
    public IReadOnlyList<QrShardFileStatus> Status()
    {
        var result = new List<QrShardFileStatus>();
        foreach (var group in _shards.GroupBy(s => s.Header.FileId))
        {
            var first = group.First().Header;
            var have = group.Where(s => !s.Header.IsParity).Select(s => s.Header.Index).ToHashSet();
            var missing = Enumerable.Range(0, first.Count).Where(i => !have.Contains(i)).ToList();
            result.Add(new QrShardFileStatus(
                first.FileName, have.Count, first.Count, group.Count(s => s.Header.IsParity),
                missing, _parity.IsSetComplete([.. group])));
        }
        return result;
    }

    /// <summary>
    /// Assembles the collected shards into the restored file(s). Throws
    /// <see cref="QrShardDecodeException"/> if the set is not yet complete (check
    /// <see cref="IsComplete"/> first) or if verification fails.
    /// </summary>
    public IReadOnlyList<QrShardDecodedFile> Assemble(string? outputPath = null, Action<string>? progress = null)
    {
        try
        {
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
