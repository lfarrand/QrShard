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

/// <summary>A decode failure; the message is user-facing and actionable.</summary>
public sealed class QrShardDecodeException(string message) : Exception(message);
