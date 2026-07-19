namespace QrShard;

/// <summary>
/// Injectable encoder: carries the configuration explicitly instead of reading the
/// <see cref="AppSettings.Current"/> global. The rendering pipeline itself stays in the static
/// <see cref="Encoder"/> (pure hot-path code shared with tests and benchmarks).
/// </summary>
internal sealed class ShardEncoder(AppSettings settings) : IShardEncoder
{
    public EncodeResult Encode(string filePath, string outDir, EncodeOptions options, Action<string>? log = null) =>
        Encoder.Encode(filePath, outDir, options, log, settings);
}

/// <summary>Injectable adapter over the static <see cref="Slideshow"/> page generator.</summary>
internal sealed class SlideshowWriter : ISlideshowWriter
{
    public string Write(string outDir, IReadOnlyList<string> imageFiles, int intervalMs) =>
        Slideshow.Write(outDir, imageFiles, intervalMs);
}
