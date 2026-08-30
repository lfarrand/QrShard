using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace QrShard.Display;

/// <summary>Bootstraps the 1:1 image player from argv and appsettings.json.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "once" may appear anywhere; the remaining arguments stay positional.
        string[] positional = [.. e.Args.Where(a => !a.Equals("once", StringComparison.OrdinalIgnoreCase))];
        bool playOnce = positional.Length != e.Args.Length;

        string? folder = positional.Length > 0 ? positional[0] : null;
        double fps = 0.5;

        if (positional.Length > 1 && !DisplayPlayback.TryParseFps(positional[1], out fps))
        {
            ShowUsage($"'{positional[1]}' is not a valid frames-per-second value.");
            return;
        }

        long memoryCap = DisplayPlayback.DefaultMemoryCapBytes;
        if (positional.Length > 2)
        {
            if (!DisplayPlayback.TryParseMemoryCap(positional[2], out memoryCap))
            {
                ShowUsage($"'{positional[2]}' is not a valid memory cap. Use e.g. 2GB, 512MB, or 4 (gigabytes).");
                return;
            }
        }

        if (folder is null || !Directory.Exists(folder))
        {
            ShowUsage(folder is null
                ? "No image folder was specified."
                : $"Folder not found: {folder}");
            return;
        }

        if (!DisplayPlayback.ValidateFps(fps))
        {
            ShowUsage("Frames per second must be greater than 0 and at most 1000.");
            return;
        }

        var screens = System.Windows.Forms.Screen.AllScreens;
        int screenIndex = LoadScreenIndexSetting("DisplayScreen");
        System.Windows.Forms.Screen? targetScreen = screenIndex switch
        {
            0 => System.Windows.Forms.Screen.PrimaryScreen ?? screens[0],
            _ when screenIndex <= screens.Length => screens[screenIndex - 1],
            _ => null,
        };
        if (targetScreen is null)
        {
            ShowUsage($"DisplayScreen {screenIndex} in appsettings.json is out of range.\n\n" +
                      $"Detected screens:\n{DescribeScreens(screens)}");
            return;
        }

        MainWindow = new MainWindow(folder, fps, memoryCap, playOnce,
            LoadColorSetting("BorderColor", Colors.Black),
            LoadColorSetting("TerminationColor", Colors.Black),
            targetScreen.Bounds);
        MainWindow.Show();
    }

    private static string DescribeScreens(System.Windows.Forms.Screen[] screens) =>
        string.Join("\n", screens.Select((s, i) =>
            $"  {i + 1}: {s.DeviceName} {s.Bounds.Width}x{s.Bounds.Height} at ({s.Bounds.X},{s.Bounds.Y})" +
            (s.Primary ? " [primary]" : "")));

    /// <summary>Reads a screen-number setting: 0 (or missing) means the primary screen.</summary>
    private static int LoadScreenIndexSetting(string key) =>
        TryGetAppSetting(key, out JsonElement element) &&
        element.TryGetInt32(out int value) && value >= 0
            ? value
            : 0;

    private static Color LoadColorSetting(string key, Color fallback)
    {
        try
        {
            if (TryGetAppSetting(key, out JsonElement element) &&
                element.GetString() is { } text &&
                ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch (Exception)
        {
            // Missing or malformed settings — fall back to the default.
        }
        return fallback;
    }

    private static bool TryGetAppSetting(string key, out JsonElement element)
    {
        element = default;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty(key, out JsonElement found))
                return false;

            element = found.Clone();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ShowUsage(string error)
    {
        MessageBox.Show(
            $"{error}\n\n" +
            "Usage: QrShard.Display <image-folder> [frames-per-second] [memory-cap] [once]\n\n" +
            "Examples:\n" +
            "  QrShard.Display C:\\Photos          (default: 0.5 fps, one image every 2s)\n" +
            "  QrShard.Display C:\\Photos 0.2      (one image every 5s)\n" +
            "  QrShard.Display C:\\Frames 500      (flipbook playback at 500 fps)\n" +
            "  QrShard.Display C:\\Frames 500 8GB  (flipbook with an 8 GB pre-decode cap)\n" +
            "  QrShard.Display C:\\Frames 10 once  (play a single pass, then hold the\n" +
            "                                    termination colour from appsettings.json)\n\n" +
            "Images are always shown pixel-perfect at full resolution. The memory cap\n" +
            "(default 2GB) guards flipbook pre-decoding; if the frames need more, the\n" +
            "app tells you the required size instead of shrinking them.\n\n" +
            "The border colour around images smaller than the monitor is set in\n" +
            "appsettings.json next to the executable.",
            "QrShard.Display", MessageBoxButton.OK, MessageBoxImage.Warning);
        Shutdown(1);
    }
}
