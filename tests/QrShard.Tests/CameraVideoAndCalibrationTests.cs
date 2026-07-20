using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>Camera-profile video decoding with pose caching, and the calibrate command.</summary>
public class CameraVideoAndCalibrationTests
{
    private static readonly EncodeOptions Camera = new()
    {
        Width = 1080, Height = 1080, CellPx = 8, BitsPerCell = 2, EccParity = 32, CameraMode = true,
    };

    [Fact]
    public void CameraRecording_HandheldDrift_DecodesWithPoseCaching()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(6_000, seed: 5);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Camera);
        Assert.True(result.ImageCount >= 2);

        // A handheld phone recording: every frame is a photo with slightly drifting pose.
        string capDir = tmp.Sub("frames");
        var framePaths = new List<string>();
        double angle = 2.6;
        foreach (string f in result.Files)
        {
            framePaths.Add(CameraCaptureTests.SimulateCameraCapture(
                f, Path.Combine(capDir, $"frame{framePaths.Count:D2}.png"),
                rotationDegrees: angle, perspective: 0.015, blurSigma: 0.5f, jpegQuality: 0));
            angle += 0.35; // drift between frames — the cached pose must absorb or refresh
        }

        var frames = framePaths.Select(Image.Load<Rgb24>).ToList();
        var root = frames[0].Clone();
        for (int i = 1; i < frames.Count; i++)
            root.Frames.AddFrame(frames[i].Frames.RootFrame);
        string recording = tmp.File("recording.png");
        root.SaveAsPng(recording);
        root.Dispose();
        frames.ForEach(f => f.Dispose());

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.StoppedEarly || stats.ShardsCollected == result.ImageCount);
    }

    [Fact]
    public void ScreenRecording_StillDecodes_WithoutCameraCost()
    {
        // The latch must not regress plain screen recordings: first frame decodes axis-aligned
        // and camera detection is never consulted (verified behaviorally by round-trip).
        using var tmp = new TempDir();
        byte[] content = TestData.Random(60_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });

        var frames = result.Files.Select(Image.Load<Rgb24>).ToList();
        var root = frames[0].Clone();
        for (int i = 1; i < frames.Count; i++)
            root.Frames.AddFrame(frames[i].Frames.RootFrame);
        string recording = tmp.File("recording.png");
        root.SaveAsPng(recording);
        root.Dispose();
        frames.ForEach(f => f.Dispose());

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out _);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Calibrate_GenerateThenAnalyzePristineCaptures_RecommendsDensest()
    {
        using var tmp = new TempDir();
        string calDir = tmp.File("cal");
        var stdout = new StringWriter();
        int genCode = new Cli().Run(["calibrate", "-o", calDir, "-r", "900"], stdout, stdout);
        Assert.Equal(0, genCode);
        Assert.True(Directory.GetFiles(calDir, "*.png").Length >= 5);

        // "Captures" = the pristine probes themselves: the densest setting must win.
        var analyzeOut = new StringWriter();
        int code = new Cli().Run(["calibrate", calDir], analyzeOut, analyzeOut);
        Assert.Equal(0, code);
        Assert.Contains("-c 1 -b 8", analyzeOut.ToString());
    }

    [Fact]
    public void Calibrate_DegradedCaptures_RecommendSomethingCoarser()
    {
        using var tmp = new TempDir();
        string calDir = tmp.File("cal");
        new Cli().Run(["calibrate", "-o", calDir, "-r", "900"], new StringWriter(), new StringWriter());

        // Simulate a mediocre capture chain: downscale to 60% and back (kills 1px cells).
        string capDir = tmp.Sub("captured");
        foreach (string f in Directory.GetFiles(calDir, "*.png"))
        {
            using var img = Image.Load<Rgb24>(f);
            int w = img.Width, h = img.Height;
            img.Mutate(c => c.Resize(w * 6 / 10, h * 6 / 10).Resize(w, h));
            img.SaveAsPng(Path.Combine(capDir, Path.GetFileName(f)));
        }

        var analyzeOut = new StringWriter();
        int code = new Cli().Run(["calibrate", capDir], analyzeOut, analyzeOut);
        string report = analyzeOut.ToString();
        Assert.Equal(0, code);
        Assert.Contains("Recommended encode settings", report);
        Assert.DoesNotContain("-c 1 -b 8", report); // max density can't survive 0.6x resampling
    }
}
