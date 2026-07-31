using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>
/// FinderDetector's minimum-module floor scaled with the PHOTO's size, not the shard's, which made
/// it an undocumented framing rule: a shard had to span roughly 21% of the frame's short side or
/// every genuine finder hit was discarded before the ratio checks ever ran.
///
/// The test isolates it exactly — one capture, pasted unchanged onto progressively larger canvases.
/// The shard pixels are byte-identical in every case; only the surround grows.
/// </summary>
public class FramingFloorTests
{
    private static readonly EncodeOptions Camera = new()
    {
        Width = 1080, Height = 1080, CellPx = 8, BitsPerCell = 3, CameraMode = true,
    };

    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)] // failed before: the floor crossed the true ~11.7 px finder module
    [InlineData(4.0)]
    public void AShardThatDoesNotFillTheFrameStillDecodes(double pad)
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(600);
        string input = tmp.WriteFile("in.bin", content);
        var enc = new ShardEncoder().Encode(input, tmp.Sub("shards"), Camera);

        string cap = tmp.File("cap.png");
        CameraCaptureTests.SimulateCameraCapture(enc.Files[0], cap, rotationDegrees: 3, perspective: 0.04,
            blurSigma: 0.6f, jpegQuality: 100);

        string padded = tmp.File("padded.png");
        using (var shot = Image.Load<Rgb24>(cap))
        {
            int w = (int)(shot.Width * pad), h = (int)(shot.Height * pad);
            using var canvas = new Image<Rgb24>(w, h, new Rgb24(90, 92, 96));
            canvas.Mutate(c => c.DrawImage(shot, new Point((w - shot.Width) / 2, (h - shot.Height) / 2), 1f));
            canvas.SaveAsPng(padded);
        }

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([padded], restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }
}
