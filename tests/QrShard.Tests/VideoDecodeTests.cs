using System.Diagnostics;
using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// Video mode: the sender-side slideshow and the receiver-side decoding of recordings —
/// duplicate frames, torn mid-transition frames, junk, early stop, and parity recovery.
/// </summary>
public class VideoDecodeTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (byte[] Content, List<string> Files) Encode(TempDir tmp, int size = 100_000,
        EncodeOptions? opt = null, int seed = 42)
    {
        byte[] content = TestData.Random(size, seed);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), opt ?? Fast);
        return (content, result.Files);
    }

    // ---------- Recording synthesis ----------

    private static Image<Rgb24> LoadFrame(string path)
    {
        var img = Image.Load<Rgb24>(path);
        return img;
    }

    private static Image<Rgb24> BlendFrames(string a, string b)
    {
        using var imgA = LoadFrame(a);
        using var imgB = LoadFrame(b);
        var blended = new Image<Rgb24>(imgA.Width, imgA.Height);
        for (int y = 0; y < imgA.Height; y++)
        {
            for (int x = 0; x < imgA.Width; x++)
            {
                var pa = imgA[x, y];
                var pb = imgB[x, y];
                blended[x, y] = new Rgb24(
                    (byte)((pa.R + pb.R) / 2), (byte)((pa.G + pb.G) / 2), (byte)((pa.B + pb.B) / 2));
            }
        }
        return blended;
    }

    private static Image<Rgb24> NoiseFrame(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var img = new Image<Rgb24>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img[x, y] = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        return img;
    }

    /// <summary>Builds an animated PNG "recording" from a frame plan (each entry produces one frame).</summary>
    private static string BuildApng(TempDir tmp, IReadOnlyList<Image<Rgb24>> frames, string name = "recording.png")
    {
        var root = frames[0].Clone();
        for (int i = 1; i < frames.Count; i++)
            root.Frames.AddFrame(frames[i].Frames.RootFrame);
        string path = tmp.File(name);
        root.SaveAsPng(path);
        root.Dispose();
        foreach (var f in frames)
            f.Dispose();
        return path;
    }

    /// <summary>A realistic recording plan: duplicates of every shard, torn transitions, junk.</summary>
    private static List<Image<Rgb24>> RecordingPlan(List<string> shardFiles, int duplicates = 2, bool torn = true, bool junk = true)
    {
        var frames = new List<Image<Rgb24>>();
        for (int i = 0; i < shardFiles.Count; i++)
        {
            for (int d = 0; d < duplicates; d++)
                frames.Add(LoadFrame(shardFiles[i]));
            if (torn && i + 1 < shardFiles.Count)
                frames.Add(BlendFrames(shardFiles[i], shardFiles[i + 1])); // mid-transition tear
        }
        if (junk)
        {
            using var probe = LoadFrame(shardFiles[0]);
            frames.Insert(0, NoiseFrame(probe.Width, probe.Height, 1));
        }
        return frames;
    }

    // ---------- Animated-image path (pure managed) ----------

    [Fact]
    public void Recording_WithDuplicatesTornAndJunk_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        Assert.True(files.Count >= 2);
        string recording = BuildApng(tmp, RecordingPlan(files));

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.Equal(files.Count, stats.ShardsCollected);
        // The duplicate pre-filter must have skipped the repeats.
        Assert.True(stats.FramesDecoded < stats.FramesExamined,
            $"expected dedupe: examined {stats.FramesExamined}, decoded {stats.FramesDecoded}");
    }

    [Fact]
    public void Recording_StopsEarly_WhenSetCompleteBeforeTail()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        var plan = RecordingPlan(files, duplicates: 1, torn: false, junk: false);
        // Long tail of extra cycles that a naive decoder would grind through.
        for (int cycle = 0; cycle < 3; cycle++)
            foreach (string f in files)
                plan.Add(LoadFrame(f));
        int totalFrames = plan.Count;
        string recording = BuildApng(tmp, plan);

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.StoppedEarly);
        Assert.True(stats.FramesExamined <= files.Count + 1,
            $"expected early stop near frame {files.Count}, examined {stats.FramesExamined} of {totalFrames}");
    }

    [Fact]
    public void Recording_MissingShardButParityPresent_RecoversAndStopsEarly()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, opt: Fast with { RecoveryPercent = 25 });
        var dataFiles = files.Where(f => !f.Contains("parity")).ToList();
        var parityFiles = files.Where(f => f.Contains("parity")).ToList();
        Assert.True(parityFiles.Count >= 1);

        // Recording that never shows data image #2 — parity must cover it.
        var shown = dataFiles.Where((_, i) => i != 1).Concat(parityFiles).ToList();
        string recording = BuildApng(tmp, RecordingPlan(shown, duplicates: 1, torn: false, junk: false));

        string output = tmp.File("out.bin");
        var log = new List<string>();
        new VideoDecoder().Decode(recording, output, 8, log.Add, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.StoppedEarly);
        Assert.Contains(log, m => m.Contains("recovered 1 missing image"));
    }

    [Fact]
    public void Recording_IncompleteSet_ReportsMissingImage()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp);
        var shown = files.Where((_, i) => i != 0).ToList(); // image 1 never displayed
        string recording = BuildApng(tmp, RecordingPlan(shown, duplicates: 1));

        var ex = Assert.Throws<ShardDecodeException>(
            () => new VideoDecoder().Decode(recording, tmp.File("out.bin"), 8, _ => { }, out _));
        Assert.Contains("missing image(s) 1", ex.Message);
    }

    [Fact]
    public void SingleFrameImage_IsNotTreatedAsVideo()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, size: 1_000);
        Assert.False(VideoDecoder.IsAnimatedImage(files[0]));
        Assert.False(VideoDecoder.IsVideoFile(files[0]));
    }

    // ---------- Completeness check ----------

    [Fact]
    public void IsSetComplete_TracksDataAndParityStripes()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, opt: Fast with { RecoveryPercent = 25 });
        var scratch = new DecodeScratch();
        var all = files.Select(f => new ShardDecoder().DecodeImage(f, scratch)).ToList();
        var data = all.Where(s => !s.Header.IsParity).ToList();
        var parity = all.Where(s => s.Header.IsParity).ToList();
        Assert.True(parity.Count >= 1);

        Assert.True(new ParityReassembler().IsSetComplete(data));                                  // all data, no parity needed
        Assert.False(new ParityReassembler().IsSetComplete(data.Skip(1).ToList()));                // one missing, no parity
        Assert.True(new ParityReassembler().IsSetComplete(data.Skip(1).Concat(parity).ToList())); // parity covers the hole
        Assert.False(new ParityReassembler().IsSetComplete([]));
    }

    // ---------- Slideshow generation ----------

    [Fact]
    public void Slideshow_EmbedsAllImagesAndInterval()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, size: 30_000);
        string path = new SlideshowWriter().Write(Path.GetDirectoryName(files[0])!, files, 350);

        string html = File.ReadAllText(path);
        Assert.Equal(files.Count, html.Split("data:image/png;base64,").Length - 1);
        Assert.Contains("const interval = 350", html);
        // Embedded payloads must be the actual files.
        Assert.Contains(Convert.ToBase64String(File.ReadAllBytes(files[0]))[..64], html);
    }

    [Fact]
    public void Slideshow_RejectsSillyIntervals() =>
        Assert.Throws<ArgumentException>(() => new SlideshowWriter().Write(Path.GetTempPath(), [], 20));

    [Fact]
    public void Cli_EncodeVideo_WritesSlideshow()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("f.bin", TestData.Random(30_000));
        string outDir = tmp.File("shards");
        var stdout = new StringWriter();
        int code = new Cli().Run(["encode", input, "-o", outDir, "-r", "900", "--video", "--interval", "250"], stdout, new StringWriter());
        Assert.Equal(0, code);
        Assert.Contains("slideshow.html", stdout.ToString());
        Assert.Contains("250 ms/image", stdout.ToString());
        Assert.True(File.Exists(Path.Combine(outDir, "slideshow.html")));
    }

    // ---------- Real video container via ffmpeg (skipped when ffmpeg is absent) ----------

    private static bool FfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void Mp4Recording_RoundTrips_WhenFfmpegPresent()
    {
        if (!FfmpegAvailable())
            return; // environment without ffmpeg: the pure-managed animated-image path covers the logic

        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, size: 60_000);

        // Build an mp4 slideshow at 2 img/s, high quality (like a decent screen recording).
        string listFile = tmp.File("frames.txt");
        File.WriteAllLines(listFile,
            files.SelectMany(f => new[] { $"file '{f.Replace('\\', '/')}'", "duration 0.5" })
                 .Append($"file '{files[^1].Replace('\\', '/')}'")); // concat demuxer ignores the last duration
        string mp4 = tmp.File("recording.mp4");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{listFile}\" " +
            "-vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2:color=white\" " + // x264 yuv420p needs even dimensions
            $"-c:v libx264 -preset veryfast -crf 15 -pix_fmt yuv420p \"{mp4}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit(120_000);
            Assert.Equal(0, p.ExitCode);
        }

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(mp4, output, 8, _ => { }, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.FramesDecoded < stats.FramesExamined); // dedupe active at 8 fps vs 2 img/s
    }
}
