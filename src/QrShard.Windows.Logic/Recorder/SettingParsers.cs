namespace QrShard.Recorder;

/// <summary>Parses Recorder App.config string values.</summary>
internal static class SettingParsers
{
    public static bool ParseBool(string? text) =>
        bool.TryParse(text, out bool value) && value;

    public static int ParseTolerance(string? text) =>
        int.TryParse(text, out int value) ? Math.Clamp(value, 0, 255) : 30;

    public static int ParseScreenIndex(string? text) =>
        int.TryParse(text, out int value) && value >= 0 ? value : 0;
}
