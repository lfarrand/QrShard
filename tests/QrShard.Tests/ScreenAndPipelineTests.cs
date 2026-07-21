using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Screen self-capture argument plumbing and the pipelined video decode path.</summary>
public class ScreenAndPipelineTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    [Fact]
    public void ScreenArgs_UsePlatformGrabber_AndMapRegions()
    {
        var (fullInput, fullFilter) = ScreenFrameSource.BuildScreenArgs(null);
        var (regionInput, regionFilter) = ScreenFrameSource.BuildScreenArgs((100, 80, 1920, 1080));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("-f gdigrab -i desktop", fullInput);
            Assert.Equal("-f gdigrab -offset_x 100 -offset_y 80 -video_size 1920x1080 -i desktop", regionInput);
            Assert.Null(regionFilter);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("avfoundation", fullInput);
            Assert.Equal("crop=1920:1080:100:80", regionFilter);
        }
        else
        {
            Assert.Contains("x11grab", fullInput);
            Assert.Contains("+100,80", regionInput);
            Assert.Contains("-video_size 1920x1080", regionInput);
        }
        Assert.Null(fullFilter);
    }

    [Theory]
    [InlineData("100,80,1920,1080", 100, 80, 1920, 1080)]
    [InlineData("0,0,800,600", 0, 0, 800, 600)]
    public void ParseRegion_AcceptsCsv(string input, int x, int y, int w, int h) =>
        Assert.Equal((x, y, w, h), ScreenFrameSource.ParseRegion(input));

    [Theory]
    [InlineData("100,80,1920")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,-5,10")]
    public void ParseRegion_RejectsMalformed(string input) =>
        Assert.Throws<ArgumentException>(() => ScreenFrameSource.ParseRegion(input));

    [Fact]
    public void ParseRegion_NullMeansWholeScreen() => Assert.Null(ScreenFrameSource.ParseRegion(null));

    [Fact]
    public void PipelinedDecode_RoundTrips_AndStopsEarly()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3);

        // Recording with duplicates plus a long tail the early stop should mostly skip.
        var frames = new List<Image<Rgb24>>();
        for (int cycle = 0; cycle < 3; cycle++)
            foreach (string f in result.Files)
            {
                frames.Add(Image.Load<Rgb24>(f));
                frames.Add(Image.Load<Rgb24>(f)); // duplicate — the pre-filter must drop it
            }
        var root = frames[0].Clone();
        for (int i = 1; i < frames.Count; i++)
            root.Frames.AddFrame(frames[i].Frames.RootFrame);
        string recording = tmp.File("recording.png");
        root.SaveAsPng(recording);
        root.Dispose();
        frames.ForEach(f => f.Dispose());

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out var stats, null, decodeWorkers: 3);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.StoppedEarly);
        // Parallel prefetch examines further past completion than the sequential path (dropped
        // duplicates bypass the queue's backpressure), so the guarantee here is only that the
        // producer was cancelled before the end of the stream.
        Assert.True(stats.FramesExamined < frames.Count,
            $"expected early stop, examined {stats.FramesExamined} of {frames.Count}");
        Assert.True(stats.FramesDecoded < stats.FramesExamined, "duplicate pre-filter must run in the producer");
    }
}
