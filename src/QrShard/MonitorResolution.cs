using System.Runtime.InteropServices;

namespace QrShard;

/// <summary>
/// Detects the primary monitor's native resolution, used as the default encode resolution so
/// shards fill the screen the capture will be taken from. EnumDisplaySettings reports the
/// display mode's physical pixels regardless of the process's DPI awareness — GetSystemMetrics
/// would return virtualized (DPI-scaled) values and undersize the shards on a 125%/150% display.
/// </summary>
internal static class MonitorResolution
{
    /// <summary>Native resolution of the primary display, or null when there is none (headless/CI).</summary>
    public static (int Width, int Height)? DetectPrimary()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode))
                return null;
            if (mode.dmPelsWidth < 1 || mode.dmPelsHeight < 1)
                return null;
            return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
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
}
