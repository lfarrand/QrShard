namespace QrShard;

/// <summary>
/// Frames from THIS machine's screen via ffmpeg's screen-grab devices — `qrshard receive
/// --screen`. Run the receiver locally and put the sender's slideshow anywhere it can see:
/// most notably inside an RDP/VM window, which transfers files out of a locked-down remote
/// desktop with no tooling, clipboard, or drive mapping on the remote side at all.
/// </summary>
internal sealed class ScreenFrameSource((int X, int Y, int W, int H)? region, int decodeMemoryBudgetMB) : IFrameSource
{
    public ScreenFrameSource((int X, int Y, int W, int H)? region)
        : this(region, AppSettings.Current.DecodeMemoryBudgetMB)
    {
    }

    public IEnumerable<Bitmap> Frames(string path, double fps,
        CancellationToken cancellationToken = default) // path is the display label only
    {
        var (input, filter) = BuildScreenArgs(region);
        return RecordingFrameSource.FfmpegPipe(input, fps, filter, decodeMemoryBudgetMB, cancellationToken);
    }

    /// <summary>Platform screen-grab input args, plus an optional filter for region cropping.</summary>
    internal static (string[] InputArgs, string? Filter) BuildScreenArgs((int X, int Y, int W, int H)? region)
    {
        if (OperatingSystem.IsWindows())
        {
            var args = new List<string> { "-f", "gdigrab" };
            if (region is (var x, var y, var w, var h))
            {
                args.AddRange(["-offset_x", x.ToString(), "-offset_y", y.ToString(),
                    "-video_size", $"{w}x{h}"]);
            }
            args.AddRange(["-i", "desktop"]);
            return ([.. args], null);
        }
        if (OperatingSystem.IsMacOS())
        {
            // avfoundation has no offset/size input options; crop in the filter graph instead.
            string? crop = region is (var x, var y, var w, var h) ? $"crop={w}:{h}:{x}:{y}" : null;
            return (["-f", "avfoundation", "-i", "Capture screen 0"], crop);
        }

        string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        if (region is (var rx, var ry, var rw, var rh))
            return (["-f", "x11grab", "-video_size", $"{rw}x{rh}", "-i", $"{display}+{rx},{ry}"], null);
        return (["-f", "x11grab", "-i", display], null);
    }

    /// <summary>Parses "x,y,w,h"; null input means the whole screen.</summary>
    internal static (int X, int Y, int W, int H)? ParseRegion(string? value)
    {
        if (value is null)
            return null;
        string[] parts = value.Split(',');
        if (parts.Length != 4 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h) || w <= 0 || h <= 0)
            throw new ArgumentException("--region must be x,y,w,h (e.g. 100,80,1920,1080).");
        return (x, y, w, h);
    }
}
