namespace QrShard;

/// <summary>
/// Static convenience facade over <see cref="ShardDecoder"/> with default wiring (settings from
/// appsettings.json, real camera rectifier). The DI-composed path (Cli via
/// <see cref="IShardDecoder"/>) does not use this; it exists for tests, benchmarks, and any
/// caller that wants the one-liner API.
/// </summary>
internal static class Decoder
{
    private static ShardDecoder Default => new(AppSettings.Current, new CameraRectifier());

    public static List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log) =>
        Default.DecodeFolder(imagePaths, outputPath, log);

    public static DecodedShard DecodeImage(string path) => DecodeImage(path, new DecodeScratch());

    public static DecodedShard DecodeImage(string path, DecodeScratch scratch) =>
        Default.DecodeImage(path, scratch);

    public static DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path) =>
        Default.DecodeBitmap(bmp, scratch, path);

    public static List<RestoredFile> Assemble(List<DecodedShard> shards, string? outputPath, Action<string> log) =>
        ShardAssembler.Assemble(shards, outputPath, log);

    public static bool IsSetComplete(IReadOnlyCollection<DecodedShard> shards) =>
        ParityReassembler.IsSetComplete(shards);
}
