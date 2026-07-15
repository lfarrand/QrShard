using System.Diagnostics;
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
            using var process = Process.Start(new ProcessStartInfo("xrandr", "--current")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? TryParseXrandr(output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null; // xrandr not installed, or no usable display
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
            return (int.Parse(primary.Groups[1].Value), int.Parse(primary.Groups[2].Value));

        var connected = ConnectedGeometry().Match(output);
        if (connected.Success)
            return (int.Parse(connected.Groups[1].Value), int.Parse(connected.Groups[2].Value));

        var active = ActiveModeLine().Match(output);
        if (active.Success)
            return (int.Parse(active.Groups[1].Value), int.Parse(active.Groups[2].Value));

        return null;
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
