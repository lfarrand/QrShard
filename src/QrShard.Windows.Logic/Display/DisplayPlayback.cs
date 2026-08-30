using System.Globalization;

namespace QrShard.Display;

/// <summary>Parses Display argv and decides flipbook versus slideshow.</summary>
internal static class DisplayPlayback
{
    public const double FlipbookThresholdFps = 5.0;
    public const double MaxFps = 1000.0;
    public const long DefaultMemoryCapBytes = 2 * ByteSizeFormat.Gibibyte;

    public static bool IsFlipbook(double fps) => fps > FlipbookThresholdFps;

    public static bool TryParseFps(string text, out double fps) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out fps);

    public static bool ValidateFps(double fps) => fps > 0 && fps <= MaxFps;

    /// <summary>Parses <c>4</c>, <c>4GB</c>, or <c>512MB</c> (bare numbers mean gigabytes).</summary>
    public static bool TryParseMemoryCap(string text, out long bytes)
    {
        bytes = 0;
        text = text.Trim();

        long multiplier = ByteSizeFormat.Gibibyte;
        if (text.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
            text = text[..^2];
        else if (text.EndsWith("MB", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = ByteSizeFormat.Mebibyte;
            text = text[..^2];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;

        bytes = (long)(value * multiplier);
        return bytes > 0;
    }
}
