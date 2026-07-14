namespace QrShard.Benchmarks;

/// <summary>
/// The size/config matrix for the transfer benchmarks. Both axes can be trimmed via
/// environment variables so a subset can be run without editing code:
///   QRSHARD_BENCH_SIZES=1KB,1MB,100MB
///   QRSHARD_BENCH_PRESETS=Default,Max4K
/// </summary>
internal static class BenchPresets
{
    public const string DefaultSizes = "1KB,10KB,100KB,500KB,1MB,10MB,100MB,250MB,500MB,1GB";
    public const string DefaultPresets = "Default,Dense,Max4K,Max4K-R10";

    /// <summary>The filename used for generated benchmark payloads (affects per-image header size).</summary>
    public const string PayloadName = "bench.bin";

    public static EncodeOptions Options(string preset) => preset switch
    {
        // Robust default: survives fractional rescaling, cursors, overlays.
        "Default" => new EncodeOptions(),
        // Pixel-perfect medium density on a normal display.
        "Dense" => new EncodeOptions { CellPx = 2, BitsPerCell = 6 },
        // Large-file choice: fills a 4K display, ~4.9 MB/image.
        "Max4K" => new EncodeOptions { Width = 3840, Height = 2160, CellPx = 1, BitsPerCell = 6 },
        // Same, plus 10% cross-shard parity images (whole-image loss protection).
        "Max4K-R10" => new EncodeOptions { Width = 3840, Height = 2160, CellPx = 1, BitsPerCell = 6, RecoveryPercent = 10 },
        _ => throw new ArgumentException($"Unknown preset '{preset}'. Known: {DefaultPresets}"),
    };

    public static long ParseSize(string label)
    {
        string s = label.Trim().ToUpperInvariant();
        long multiplier = 1;
        if (s.EndsWith("GB"))
            (s, multiplier) = (s[..^2], 1024L * 1024 * 1024);
        else if (s.EndsWith("MB"))
            (s, multiplier) = (s[..^2], 1024L * 1024);
        else if (s.EndsWith("KB"))
            (s, multiplier) = (s[..^2], 1024L);
        return long.Parse(s) * multiplier;
    }

    /// <summary>
    /// Image counts are deterministic for incompressible payloads, so the report can compute
    /// them without instrumenting the benchmark processes.
    /// </summary>
    public static (int DataImages, int ParityImages) EstimateImages(string preset, long sizeBytes)
    {
        var opt = Options(preset);
        var layout = Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity);
        long capacity = layout.UsableBytes - ShardHeader.Size(PayloadName);
        int data = (int)Math.Max(1, (sizeBytes + capacity - 1) / capacity);
        var (stripeData, stripeParity) = Encoder.PlanStripes(data, opt.RecoveryPercent);
        int stripes = stripeParity > 0 ? (data + stripeData - 1) / stripeData : 0;
        return (data, stripes * stripeParity);
    }
}
