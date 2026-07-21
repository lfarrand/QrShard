using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Auto-escalating extraction fps and the camera-frame sharpness gate.</summary>
public class VideoEscalationTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    /// <summary>Frame source that yields a different slice of the shard set per requested fps,
    /// simulating a recording that reveals more of the cycle when sampled faster.</summary>
    private sealed class FpsAwareSource(List<string> files) : IFrameSource
    {
        public int Passes { get; private set; }

        public IEnumerable<Bitmap> Frames(string path, double fps)
        {
            Passes++;
            // fps 8 → first half only (incomplete); fps ≥ 16 → all images.
            var shown = fps >= 16 ? files : files.Take((files.Count + 1) / 2).ToList();
            foreach (string f in shown)
            {
                using var img = Image.Load<Rgb24>(f);
                var px = new Rgb24[img.Width * img.Height];
                img.CopyPixelDataTo(px);
                yield return new Bitmap(px, img.Width, img.Height);
            }
        }
    }

    private static VideoDecoder MakeDecoder(IFrameSource source) =>
        new(new ShardDecoder(), source, new ShardAssembler(), new ParityReassembler(), new CameraRectifier());

    [Fact]
    public void EscalateFps_ReExtractsHigher_WhenFirstPassIncomplete()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 4);

        var source = new FpsAwareSource(result.Files);
        var decoder = MakeDecoder(source);
        string output = tmp.File("out.bin");
        var log = new List<string>();
        decoder.Decode("recording.mp4", output, 8, log.Add, out var stats, escalateFps: true);

        Assert.True(source.Passes >= 2, "should have re-extracted at a higher fps");
        Assert.Contains(log, m => m.Contains("re-extracting at 16"));
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void NoEscalation_WhenDisabled_LeavesSetIncomplete()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        var source = new FpsAwareSource(result.Files);
        var decoder = MakeDecoder(source);
        // Single pass, half the images, no parity → assembly fails with a missing-image error.
        Assert.Throws<ShardDecodeException>(
            () => decoder.Decode("recording.mp4", tmp.File("out.bin"), 8, _ => { }, out _, escalateFps: false));
        Assert.Equal(1, source.Passes);
    }

    [Fact]
    public void FocusEnergy_HighForSharpEdges_LowForBlur()
    {
        // Sharp: alternating black/white columns → large horizontal gradients.
        var sharp = new Rgb24[200 * 200];
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 200; x++)
                sharp[y * 200 + x] = (x & 1) == 0 ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255);
        long sharpEnergy = VideoDecoder.FocusEnergy(new Bitmap(sharp, 200, 200));

        // Blurred: a smooth gradient → negligible local gradient.
        var blur = new Rgb24[200 * 200];
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 200; x++)
            {
                byte v = (byte)(x * 255 / 199);
                blur[y * 200 + x] = new Rgb24(v, v, v);
            }
        long blurEnergy = VideoDecoder.FocusEnergy(new Bitmap(blur, 200, 200));

        Assert.True(sharpEnergy > VideoDecoder.BlurRejectThreshold, $"sharp energy {sharpEnergy}");
        Assert.True(blurEnergy < VideoDecoder.BlurRejectThreshold, $"blur energy {blurEnergy}");
    }
}
