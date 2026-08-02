using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QrShard;

/// <summary>
/// Detects the primary monitor's native resolution, used as the default encode resolution so
/// shards fill the screen the capture will be taken from. Per-platform, all best-effort:
///
///  - Windows: EnumDisplaySettings, which reports the display mode's physical pixels regardless
///    of the process's DPI awareness — GetSystemMetrics would return virtualized (DPI-scaled)
///    values and undersize the shards on a 125%/150% display.
///  - Linux: parse `xrandr --current` (works on X11 and, via XWayland, most Wayland desktops).
///  - macOS: CoreGraphics display-mode pixel dimensions (Retina-aware — the pixel size, not
///    the scaled point size).
///
/// Headless or undetectable environments return null and the caller falls back.
/// </summary>
internal static partial class MonitorResolution
{
    /// <summary>Native resolution of the primary display, or null when there is none (headless/CI).</summary>
    public static (int Width, int Height)? DetectPrimary()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DetectWindows();
            if (OperatingSystem.IsLinux())
                return DetectLinux();
            if (OperatingSystem.IsMacOS())
                return DetectMacOS();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        return null;
    }

    // ---------- Windows ----------

    private static (int, int)? DetectWindows()
    {
        var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode))
            return null;
        if (mode.dmPelsWidth < 1 || mode.dmPelsHeight < 1)
            return null;
        return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
    }

    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    // ---------- Linux (X11 / XWayland) ----------

    private static (int, int)? DetectLinux()
    {
        // No display server session at all — don't bother spawning a process.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return null;

        try
        {
            string? executable = ExternalToolResolver.Resolve("xrandr");
            if (executable is null)
                return null;
            ProcessStartInfo start = ExternalToolResolver.CreateStartInfo(executable);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.ArgumentList.Add("--current");
            using var process = Process.Start(start);
            if (process is null)
                return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Task<(string Text, bool Truncated)> stdout =
                ReadBoundedAsync(process.StandardOutput, 1_000_000, timeout.Token);
            Task<(string Text, bool Truncated)> stderr =
                ReadBoundedAsync(process.StandardError, 16_384, timeout.Token);
            Task exit = process.WaitForExitAsync(timeout.Token);
            try
            {
                Task.WhenAll(exit, stdout, stderr).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return null;
            }
            if (process.ExitCode != 0 || stdout.Result.Truncated)
                return null;
            return TryParseXrandr(stdout.Result.Text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null; // xrandr not installed, or no usable display
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader, int retainedChars, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder(Math.Min(retainedChars, 4096));
        var buffer = new char[4096];
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return (text.ToString(), truncated);
            int keep = Math.Min(read, retainedChars - text.Length);
            if (keep > 0)
                text.Append(buffer, 0, keep);
            if (keep != read)
                truncated = true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(1_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                         System.ComponentModel.Win32Exception or
                                         NotSupportedException)
        {
            // Best effort; monitor detection is optional.
        }
    }

    /// <summary>
    /// Extracts the primary output's geometry from `xrandr --current` output. Preference order:
    /// the "connected primary WxH+X+Y" line, then any "connected WxH+X+Y" line, then the
    /// active mode line (marked with `*`).
    /// </summary>
    internal static (int Width, int Height)? TryParseXrandr(string output)
    {
        var primary = PrimaryGeometry().Match(output);
        if (primary.Success)
            return TryParseDimensions(primary);

        var connected = ConnectedGeometry().Match(output);
        if (connected.Success)
            return TryParseDimensions(connected);

        var active = ActiveModeLine().Match(output);
        if (active.Success)
            return TryParseDimensions(active);

        return null;
    }

    private static (int Width, int Height)? TryParseDimensions(Match match)
    {
        // This is untrusted helper output. Avoid Parse throwing on an arbitrarily long digit
        // sequence, and reject dimensions far beyond any display/layout we could use. Detection
        // is best-effort; a malformed response must fall back rather than abort CLI startup.
        const int maxPlausibleMonitorDimension = Layout.MaxResolution * 4;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int width) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int height) ||
            width is < 1 or > maxPlausibleMonitorDimension ||
            height is < 1 or > maxPlausibleMonitorDimension)
            return null;
        return (width, height);
    }

    [GeneratedRegex(@"^\S+ connected primary (\d+)x(\d+)\+", RegexOptions.Multiline)]
    private static partial Regex PrimaryGeometry();

    [GeneratedRegex(@"^\S+ connected (\d+)x(\d+)\+", RegexOptions.Multiline)]
    private static partial Regex ConnectedGeometry();

    [GeneratedRegex(@"^\s+(\d+)x(\d+)\s[^\r\n]*\*", RegexOptions.Multiline)]
    private static partial Regex ActiveModeLine();

    // ---------- macOS (CoreGraphics) ----------

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private static (int, int)? DetectMacOS()
    {
        nint mode = CGDisplayCopyDisplayMode(CGMainDisplayID());
        if (mode == 0)
            return null;
        try
        {
            // Pixel dimensions of the mode — the Retina framebuffer size, not the scaled points.
            int width = (int)CGDisplayModeGetPixelWidth(mode);
            int height = (int)CGDisplayModeGetPixelHeight(mode);
            return width > 0 && height > 0 ? (width, height) : null;
        }
        finally
        {
            CGDisplayModeRelease(mode);
        }
    }

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(nint mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelHeight(nint mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(nint mode);
}
