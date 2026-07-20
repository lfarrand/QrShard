using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>
/// Phase-1 camera support: shards encoded with --camera carry finder patterns and decode from
/// simulated photos — rotated, perspective-distorted, blurred, JPEG-compressed captures on a
/// desk-like background.
/// </summary>
public class CameraCaptureTests
{
    private static readonly EncodeOptions Camera = new()
    {
        Width = 1080, Height = 1080, CellPx = 8, BitsPerCell = 2, EccParity = 32, CameraMode = true,
    };

    private static (byte[] Content, List<string> Files) Encode(TempDir tmp, int size = 4_000, int seed = 77)
    {
        byte[] content = TestData.Random(size, seed);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Camera);
        return (content, result.Files);
    }

    private static void AssertDecodes(TempDir tmp, IEnumerable<string> images, byte[] expected)
    {
        string output = tmp.File($"out-{Guid.NewGuid().ToString("N")[..8]}.bin");
        new ShardDecoder().DecodeFolder(images, output, _ => { });
        Assert.Equal(expected, File.ReadAllBytes(output));
    }

    /// <summary>
    /// Simulates photographing the displayed shard: rotate + perspective-warp onto a larger
    /// desk-gray canvas (via an independent forward use of the homography math), optional
    /// radial lens distortion (barrel > 0, pincushion < 0), vignette darkening toward the
    /// corners, a lateral glare/brightness gradient, then optical blur and JPEG compression.
    /// </summary>
    internal static string SimulateCameraCapture(string srcPath, string dstPath,
        double rotationDegrees, double perspective, float blurSigma = 0.6f, int jpegQuality = 88,
        double barrel = 0, double vignette = 0, double glare = 0)
    {
        using var image = Image.Load<Rgb24>(srcPath);
        int w = image.Width, h = image.Height;
        var srcPx = new Rgb24[w * h];
        image.CopyPixelDataTo(srcPx);

        int canvasSize = (int)(Math.Sqrt((double)w * w + (double)h * h) * 1.12);
        double cx = canvasSize / 2.0, cy = canvasSize / 2.0;
        double radius = canvasSize / 2.0;
        double theta = rotationDegrees * Math.PI / 180;

        (double X, double Y) Place(double x, double y)
        {
            double dx = (x - w / 2.0) * 0.92, dy = (y - h / 2.0) * 0.92;
            return (cx + dx * Math.Cos(theta) - dy * Math.Sin(theta),
                    cy + dx * Math.Sin(theta) + dy * Math.Cos(theta));
        }

        Span<(double X, double Y)> dstCorners = [Place(0, 0), Place(w, 0), Place(w, h), Place(0, h)];
        // Asymmetric perspective: the top of the screen leans away from the camera.
        dstCorners[0] = (dstCorners[0].X + perspective * w, dstCorners[0].Y + perspective * h * 0.5);
        dstCorners[1] = (dstCorners[1].X - perspective * w * 0.7, dstCorners[1].Y + perspective * h * 0.35);
        Span<(double X, double Y)> srcCorners = [(0, 0), (w, 0), (w, h), (0, h)];

        var toSource = Homography.Solve(dstCorners, srcCorners);
        var background = new Rgb24(201, 204, 209);
        var canvasPx = new Rgb24[canvasSize * canvasSize];
        for (int y = 0; y < canvasSize; y++)
        {
            for (int x = 0; x < canvasSize; x++)
            {
                // Radial lens distortion in photo space: the content the camera "sees" at this
                // pixel actually comes from a radially displaced position.
                double px = x + 0.5, py = y + 0.5;
                double rx = (px - cx) / radius, ry = (py - cy) / radius;
                double r2 = rx * rx + ry * ry;
                double qx = cx + (px - cx) * (1 + barrel * r2);
                double qy = cy + (py - cy) * (1 + barrel * r2);

                var (sx, sy) = toSource.Apply(qx, qy);
                var color = sx < -1 || sy < -1 || sx > w || sy > h
                    ? background
                    : Bilinear(srcPx, w, h, sx - 0.5, sy - 0.5);

                double brightness = (1 - vignette * r2) * (1 - glare * (px / canvasSize));
                canvasPx[y * canvasSize + x] = new Rgb24(
                    (byte)Math.Clamp(color.R * brightness, 0, 255),
                    (byte)Math.Clamp(color.G * brightness, 0, 255),
                    (byte)Math.Clamp(color.B * brightness, 0, 255));
            }
        }

        using var canvas = Image.LoadPixelData<Rgb24>(canvasPx, canvasSize, canvasSize);
        if (blurSigma > 0)
            canvas.Mutate(c => c.GaussianBlur(blurSigma));
        if (jpegQuality > 0)
            canvas.SaveAsJpeg(dstPath, new JpegEncoder { Quality = jpegQuality });
        else
            canvas.SaveAsPng(dstPath);
        return dstPath;
    }

    private static Rgb24 Bilinear(Rgb24[] px, int w, int h, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        Rgb24 At(int xx, int yy) => px[Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)];
        var p00 = At(x0, y0);
        var p10 = At(x0 + 1, y0);
        var p01 = At(x0, y0 + 1);
        var p11 = At(x0 + 1, y0 + 1);
        byte Mix(byte a, byte b, byte c, byte d)
        {
            double top = a + (b - a) * fx, bottom = c + (d - c) * fx;
            return (byte)Math.Clamp(top + (bottom - top) * fy + 0.5, 0, 255);
        }
        return new Rgb24(Mix(p00.R, p10.R, p01.R, p11.R), Mix(p00.G, p10.G, p01.G, p11.G), Mix(p00.B, p10.B, p01.B, p11.B));
    }

    private static string CaptureDir(TempDir tmp, List<string> files, double rotation, double perspective,
        float blurSigma = 0.6f, int jpegQuality = 88, double barrel = 0, double vignette = 0, double glare = 0)
    {
        string dir = tmp.Sub($"cap-{Guid.NewGuid().ToString("N")[..6]}");
        foreach (string f in files)
            SimulateCameraCapture(f, Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + ".jpg"),
                rotation, perspective, blurSigma, jpegQuality, barrel, vignette, glare);
        return dir;
    }

    // ---------- Decoding photos ----------

    [Fact]
    public void PristineCapture_DecodesViaScreenshotPath()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        AssertDecodes(tmp, files, content);
    }

    [Fact]
    public void RotatedPerspectivePhoto_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 3, perspective: 0.04);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void StrongPerspectivePhoto_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 7, perspective: 0.08);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedByQuarterTurns_OrientationTickResolves(double degrees)
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, size: 1_500);
        string captures = CaptureDir(tmp, files, rotation: degrees + 2, perspective: 0.03);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void BlurrierLowerQualityPhoto_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, size: 1_500);
        string captures = CaptureDir(tmp, files, rotation: 2, perspective: 0.03, blurSigma: 1.0f, jpegQuality: 75);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    // ---------- Phase 2: lens distortion + illumination ----------

    [Fact]
    public void BarrelDistortedPhoto_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 2, perspective: 0.03, barrel: 0.06);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void PincushionDistortedPhoto_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 2, perspective: 0.03, barrel: -0.05);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void VignetteAndGlareGradient_Decodes()
    {
        // Brightness varies from full to ~55% across the photo; the per-region black/white
        // normalization from the traced frame must flatten it before color classification.
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 2, perspective: 0.03, vignette: 0.4, glare: 0.25);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void HandheldCombo_DistortionVignetteBlurJpeg_Decodes()
    {
        // Everything at once: the realistic handheld-phone case phase 2 exists for.
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captures = CaptureDir(tmp, files, rotation: 5, perspective: 0.05,
            blurSigma: 0.8f, jpegQuality: 80, barrel: 0.05, vignette: 0.35, glare: 0.2);
        AssertDecodes(tmp, Directory.EnumerateFiles(captures), content);
    }

    [Fact]
    public void ScreenshotProfilePhoto_FailsCleanly()
    {
        // No finder patterns: a photo of a screenshot-profile shard cannot be rectified and
        // must produce a decode error, not a crash or a wrong file.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(2_000, seed: 5);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900 });
        string photo = SimulateCameraCapture(result.Files[0], tmp.File("photo.jpg"), 4, 0.05);
        Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(photo));
    }

    // ---------- Building blocks ----------

    [Fact]
    public void Homography_MapsCorrespondencesExactly()
    {
        Span<(double X, double Y)> from = [(0, 0), (100, 0), (100, 80), (0, 80)];
        Span<(double X, double Y)> to = [(13, 7), (110, 22), (95, 90), (5, 70)];
        var h = Homography.Solve(from, to);
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = h.Apply(from[i].X, from[i].Y);
            Assert.Equal(to[i].X, x, 6);
            Assert.Equal(to[i].Y, y, 6);
        }
    }

    [Fact]
    public void Homography_CollinearPoints_Throw()
    {
        Span<(double X, double Y)> from = [(0, 0), (10, 10), (20, 20), (30, 30)];
        Span<(double X, double Y)> to = [(0, 0), (1, 0), (1, 1), (0, 1)];
        var f = from.ToArray();
        var t = to.ToArray();
        Assert.Throws<ShardDecodeException>(() => Homography.Solve(f, t));
    }

    [Fact]
    public void CameraLayout_ReservesFinderBands_WithinRequestedSize()
    {
        var layout = Layout.Create(1080, 1080, 8, 2, 32, cameraFinders: true);
        Assert.True(layout.CameraFinders);
        Assert.True(layout.FinderModule >= 8);
        Assert.Equal(layout.FinderModule * Layout.FinderBandModules, layout.FinderBand);
        Assert.True(layout.Height <= 1080);
        Assert.True(layout.Width <= 1080);

        var screenshot = Layout.Create(1080, 1080, 8, 2, 32);
        Assert.False(screenshot.CameraFinders);
        Assert.Equal(0, screenshot.FinderBand);
        Assert.True(screenshot.Height > layout.Height - 2 * layout.FinderBand); // bands cost capacity
    }

    [Fact]
    public void Rectifier_ReturnsNull_OnPlainImage()
    {
        var px = new Rgb24[400 * 400];
        Array.Fill(px, new Rgb24(220, 220, 220));
        Assert.Null(new CameraRectifier().TryRectify(new Bitmap(px, 400, 400)));
    }
}
