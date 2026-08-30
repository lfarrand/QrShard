namespace QrShard.Display;

/// <summary>Formats a byte count for the flipbook memory-cap dialog.</summary>
internal static class ByteSizeFormat
{
    internal const long Gibibyte = 1024L * 1024 * 1024;
    internal const long Mebibyte = 1024L * 1024;

    public static string Format(long bytes) =>
        bytes >= Gibibyte
            ? $"{bytes / (double)Gibibyte:0.##} GB"
            : $"{bytes / (double)Mebibyte:0.##} MB";
}
