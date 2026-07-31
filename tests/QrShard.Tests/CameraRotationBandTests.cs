namespace QrShard.Tests;

/// <summary>
/// Camera captures rotated roughly 33-55 degrees in-plane could not be decoded at all.
///
/// FinderDetector measures the module from HORIZONTAL scanlines, so a shard rotated in-plane by
/// phi reports it inflated by 1/cos(phi) — a row crossing a band of width m whose normal is turned
/// away from x traverses m/cos(phi) pixels. The error folds with 90-degree symmetry (at 90 the
/// scan simply measures the other axis) and peaks at 45 degrees, where the module comes back 41%
/// too large.
///
/// ResolveOrientation then places its probe SEVEN modules along the top edge, so a 19% overestimate
/// displaces it by 1.33 modules against a disc of radius 0.8 — off the tick entirely. DarkFraction
/// drops under 0.6, ResolveOrientation returns null, DetectPose returns null, and the capture is
/// refused. It failed closed, not degraded, and the message named nothing that would lead a user to
/// straighten the camera.
///
/// Measured end to end before the fix: 25 and 30 degrees decoded, 33 through 55 all failed, 58 and
/// 90 decoded again — exactly the 1/cos signature.
/// </summary>
public class CameraRotationBandTests
{
    private static readonly EncodeOptions Camera = new()
    {
        Width = 1080, Height = 1080, CellPx = 4, BitsPerCell = 3, CameraMode = true,
    };

    [Theory]
    [InlineData(35)]
    [InlineData(45)] // worst case: 1/cos(45) = 1.414, the module 41% over
    [InlineData(55)]
    public void ACaptureRotatedIntoTheOldDeadBandStillDecodes(int rotationDegrees)
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(3000);
        string input = tmp.WriteFile("in.bin", content);
        var encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"), Camera);

        string photo = tmp.File("photo.jpg");
        CameraCaptureTests.SimulateCameraCapture(encoded.Files[0], photo, rotationDegrees, perspective: 0.03);

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([photo], restored, _ => { });

        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void TheModuleCorrectionIsSymmetricAcrossTheQuarterTurn()
    {
        // The inflation folds with 90-degree symmetry, so the correction must too: 45 and 135
        // degrees are the same capture geometry as far as an axis-aligned scan is concerned, and
        // both must decode. This pins the fold rather than just one point of it.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(3000);
        string input = tmp.WriteFile("in.bin", content);
        var encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"), Camera);

        foreach (int rot in new[] { 45, 135 })
        {
            string photo = tmp.File($"photo{rot}.jpg");
            CameraCaptureTests.SimulateCameraCapture(encoded.Files[0], photo, rot, perspective: 0.03);
            string restored = tmp.File($"out{rot}.bin");
            new ShardDecoder().DecodeFolder([photo], restored, _ => { });
            Assert.Equal(content, File.ReadAllBytes(restored));
        }
    }
}
