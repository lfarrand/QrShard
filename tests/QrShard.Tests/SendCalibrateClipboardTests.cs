using System.Buffers.Binary;
using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>The send command, camera calibration probes, and clipboard DIB decoding.</summary>
public class SendCalibrateClipboardTests
{
    [Fact]
    public void Send_EncodesWithSlideshow_AndRespectsLaunchSuppression()
    {
        Environment.SetEnvironmentVariable("QRSHARD_NO_LAUNCH", "1");
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(40_000));
        string shardDir = tmp.File("shards");
        var stdout = new StringWriter();

        int code = new Cli().Run(["send", input, "-o", shardDir, "-r", "900"], stdout, stdout);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(shardDir, "slideshow.html")));
        Assert.Contains("suppressed by QRSHARD_NO_LAUNCH", stdout.ToString());
    }

    [Fact]
    public void CalibrateCamera_GeneratesCameraProbes_ThatDiagnoseFromPhotos()
    {
        using var tmp = new TempDir();
        string calDir = tmp.File("cal");
        var stdout = new StringWriter();
        int code = new Cli().Run(["calibrate", "--camera", "-o", calDir, "-r", "1080"], stdout, stdout);
        Assert.Equal(0, code);
        Assert.Contains("--camera", stdout.ToString());
        var probes = Directory.GetFiles(calDir, "*.png");
        Assert.True(probes.Length >= 4);

        // Simulate photographing the coarsest probe and analyze: Diagnose must rectify it.
        string capDir = tmp.Sub("captured");
        string coarsest = probes.OrderByDescending(f => f).First(); // cal-c8/c10 sort last
        CameraCaptureTests.SimulateCameraCapture(coarsest, Path.Combine(capDir, "photo.png"),
            rotationDegrees: 3, perspective: 0.02, jpegQuality: 0);

        var analyzeOut = new StringWriter();
        code = new Cli().Run(["calibrate", capDir], analyzeOut, analyzeOut);
        Assert.Equal(0, code);
        Assert.Contains("Recommended encode settings", analyzeOut.ToString());
    }

    [Fact]
    public void ParseDib_RoundTripsBothOrientations_AndDecodesAShard()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(10_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });
        using var img = Image.Load<Rgb24>(result.Files[0]);
        var px = new Rgb24[img.Width * img.Height];
        img.CopyPixelDataTo(px);

        foreach (bool bottomUp in new[] { true, false })
        {
            byte[] dib = BuildDib(px, img.Width, img.Height, bottomUp);
            var bmp = ClipboardReader.ParseDib(dib);
            Assert.NotNull(bmp);
            Assert.Equal(img.Width, bmp!.Width);
            Assert.Equal(px[0], bmp.Px[0]);
            Assert.Equal(px[^1], bmp.Px[^1]);

            // The parsed clipboard bitmap decodes like any capture.
            var shard = new ShardDecoder().DecodeBitmap(bmp, new DecodeScratch(), "clipboard");
            Assert.Equal(0, shard.Header.Index);
        }
    }

    [Fact]
    public void ParseDib_RejectsMalformedHeaders()
    {
        Assert.Null(ClipboardReader.ParseDib(new byte[10]));
        var junk = new byte[100];
        BinaryPrimitives.WriteInt32LittleEndian(junk, 40);
        BinaryPrimitives.WriteInt32LittleEndian(junk.AsSpan(4), -5); // negative width
        Assert.Null(ClipboardReader.ParseDib(junk));
    }

    /// <summary>Packs pixels as a CF_DIB: 40-byte BITMAPINFOHEADER + 24-bit BGR rows, 4-byte padded.</summary>
    private static byte[] BuildDib(Rgb24[] px, int width, int height, bool bottomUp)
    {
        int stride = (width * 3 + 3) & ~3;
        var dib = new byte[40 + stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), bottomUp ? height : -height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24); // bits
        for (int y = 0; y < height; y++)
        {
            int destY = bottomUp ? height - 1 - y : y;
            for (int x = 0; x < width; x++)
            {
                var p = px[y * width + x];
                int i = 40 + destY * stride + x * 3;
                dib[i] = p.B;
                dib[i + 1] = p.G;
                dib[i + 2] = p.R;
            }
        }
        return dib;
    }
}
