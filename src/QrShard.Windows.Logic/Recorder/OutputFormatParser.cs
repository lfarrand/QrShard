namespace QrShard.Recorder;

/// <summary>Normalizes the Recorder <c>OutputFormat</c> setting to png, tiff, or bmp.</summary>
internal static class OutputFormatParser
{
    public static string? TryNormalize(string? text) =>
        (text?.Trim().ToLowerInvariant() ?? "png") switch
        {
            "png" => "png",
            "tif" or "tiff" => "tiff",
            "bmp" => "bmp",
            _ => null,
        };
}
