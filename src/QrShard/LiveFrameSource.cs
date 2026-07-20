namespace QrShard;

/// <summary>
/// Frames from a live capture device via ffmpeg — the receiver end of `qrshard receive`:
/// point a webcam (or a capture card) at the sender's slideshow and decode in real time.
/// The "path" given to <see cref="Frames"/> is the device spec; early stop kills ffmpeg,
/// which ends the capture the moment the transfer completes.
/// </summary>
internal sealed class LiveFrameSource(string? format) : IFrameSource
{
    public IEnumerable<Bitmap> Frames(string device, double fps) =>
        RecordingFrameSource.FfmpegPipe(BuildInputArgs(format, device), fps);

    /// <summary>ffmpeg input arguments for the platform's capture framework.</summary>
    internal static string BuildInputArgs(string? format, string device)
    {
        string fmt = format ?? DefaultFormat();
        // DirectShow wants "video=<name>"; accept a bare device name for convenience.
        if (fmt == "dshow" && !device.Contains('='))
            device = $"video={device}";
        return $"-f {fmt} -i \"{device}\"";
    }

    internal static string DefaultFormat() =>
        OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "v4l2";

    /// <summary>Platform default device, or null when one must be named explicitly.</summary>
    internal static string? DefaultDevice() =>
        OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS() ? "0" : "/dev/video0";
}
