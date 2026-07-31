using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>One successfully decoded shard image: its header, verified payload, and provenance.</summary>
internal sealed record DecodedShard(ShardHeader Header, byte[] Payload, string SourceFile, int EccParity, int CorrectedBytes);

/// <summary>
/// The measured calibration palettes: the better strip (classic path) plus both strips and
/// whether they diverge enough — and are both individually trustworthy enough — that per-row
/// interpolation between them should drive classification (vertical illumination gradients).
/// </summary>
internal sealed record PaletteSet(Rgb24[] Best, Rgb24[] Top, Rgb24[] Bottom, bool Interpolate);

/// <summary>A capture whose cells were read but whose ECC/CRC failed — raw material for fusion.</summary>
internal sealed record FailedCapture(Layout Layout, byte[] Cells, string SourceFile);

/// <summary>A detected camera pose (the oriented finder quad), cacheable across video frames.</summary>
internal sealed record CameraPose(OrientedQuad Quad);

/// <summary>One file written by a decode run.</summary>
internal sealed record RestoredFile(string FileName, string OutputPath, long Length);

/// <summary>Any failure while decoding a capture; the message is user-facing and actionable.</summary>
internal sealed class ShardDecodeException(string message) : Exception(message);

/// <summary>
/// Diagnostic capture of a single-image decode attempt: the located layout, per-codeword ECC
/// correction counts (0 = clean, -1 = uncorrectable), and the outcome.
/// </summary>
internal sealed class DecodeDiagnostics
{
    public Layout? Layout { get; set; }
    public int[] CodewordErrors { get; set; } = [];
    public DecodedShard? Shard { get; set; }
    public string? Error { get; set; }

    /// <summary>Copy of the sampled cell stream when the decode failed after grid sampling — fusion input.</summary>
    public byte[]? Cells { get; set; }

    /// <summary>
    /// The Layout that produced <see cref="Cells"/>, captured at the same instant.
    ///
    /// <see cref="Layout"/> is overwritten on every decode attempt, but Cells deliberately keeps
    /// the FIRST failing attempt's buffer. An image that fails axis-aligned and then fails again
    /// after camera rectification has two independent layouts — both read from attacker-controlled
    /// metadata strips on different regions of the image — so pairing the later Layout with the
    /// earlier Cells described a buffer that was never sized for it, and PhotoFusion indexes by
    /// the layout it is handed.
    /// </summary>
    public Layout? CellsLayout { get; set; }

    /// <summary>Per-cell classification margin (squared palette distance of the winning sample),
    /// captured even when the decode fails — drives the capture-quality heatmap.</summary>
    public int[]? CellMargins { get; set; }

    /// <summary>
    /// Whether the caller actually wants <see cref="CellMargins"/> and
    /// <see cref="CodewordErrors"/>. Only the `diagnose` command reads them.
    ///
    /// Folder decode passes a live diagnostics object solely so the failure handler can fill
    /// PhotoFusion's input, which reads just <see cref="Layout"/> and <see cref="Cells"/> — but
    /// merely being non-null also switched on an int[GridW*GridH] and an int[CodewordCount] per
    /// image. Both are sized from the attacker-supplied metadata strip, both recur on the
    /// camera-rectified retry, and both were happening on every one of up to 24 workers at once,
    /// for arrays nobody then looked at.
    /// </summary>
    public bool WantDetail { get; init; }
}
