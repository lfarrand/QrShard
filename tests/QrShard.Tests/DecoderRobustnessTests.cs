using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>
/// Decoder behavior under imperfect captures: padding, rescaling, cropping, dark surroundings,
/// corruption, and incomplete or mixed shard sets.
/// </summary>
public class DecoderRobustnessTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    // ECC would silently repair the small corruptions these tests inject, so the
    // corruption-detection tests encode without it.
    private static readonly EncodeOptions NoEcc = Fast with { EccParity = 0 };

    private static (byte[] Content, List<string> Files) Encode(TempDir tmp, int size = 30_000, EncodeOptions? opt = null, int seed = 42)
    {
        byte[] content = TestData.Random(size, seed);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), opt ?? Fast);
        return (content, result.Files);
    }

    private static void AssertDecodes(TempDir tmp, IEnumerable<string> images, byte[] expected)
    {
        string output = tmp.File($"out-{Guid.NewGuid().ToString("N")[..8]}.bin");
        new ShardDecoder().DecodeFolder(images, output, _ => { });
        Assert.Equal(expected, File.ReadAllBytes(output));
    }

    /// <summary>Loads a shard image, applies a transform (which may return a new image), saves to dst.</summary>
    private static string Capture(string srcPath, string dstPath, Func<Image<Rgb24>, Image<Rgb24>> transform)
    {
        using var src = Image.Load<Rgb24>(srcPath);
        using var dst = transform(src);
        dst.SaveAsPng(dstPath);
        return dstPath;
    }

    /// <summary>Simulates a screenshot: the code pasted onto a larger canvas, optionally rescaled.</summary>
    private static Image<Rgb24> PadOnto(Image<Rgb24> src, Rgb24 background, double scale)
    {
        var canvas = new Image<Rgb24>(src.Width + 190, src.Height + 240, background);
        canvas.Mutate(c =>
        {
            c.DrawImage(src, new Point(73, 41), 1f);
            if (scale != 1.0)
                c.Resize((int)(canvas.Width * scale), 0, KnownResamplers.Bicubic);
        });
        return canvas;
    }

    private static void FillRect(Image<Rgb24> img, Rectangle r, Rgb24 color)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = r.Left; x < r.Right; x++)
                    row[x] = color;
            }
        });
    }

    // ---------- Imperfect captures that must still decode ----------

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void PaddedAndRescaledCapture_Decodes(double scale)
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string capDir = tmp.Sub("captures");
        foreach (string f in files)
            Capture(f, Path.Combine(capDir, Path.GetFileName(f)),
                img => PadOnto(img, new Rgb24(226, 229, 233), scale));
        AssertDecodes(tmp, Directory.EnumerateFiles(capDir), content);
    }

    [Fact]
    public void TightlyCroppedCapture_Decodes()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string capDir = tmp.Sub("captures");
        foreach (string f in files)
        {
            // Crop away most of the quiet zone (leave 4 of the 12 px).
            Capture(f, Path.Combine(capDir, Path.GetFileName(f)), img =>
            {
                img.Mutate(c => c.Crop(new Rectangle(8, 8, img.Width - 16, img.Height - 16)));
                return img.Clone();
            });
        }
        AssertDecodes(tmp, Directory.EnumerateFiles(capDir), content);
    }

    [Fact]
    public void CaptureOnDarkBackground_Decodes()
    {
        // A dark surround forms a larger ring-shaped frame candidate than the real frame; the
        // decoder must fall through to the correct one when the metadata strip fails to validate.
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string capDir = tmp.Sub("captures");
        foreach (string f in files)
            Capture(f, Path.Combine(capDir, Path.GetFileName(f)),
                img => PadOnto(img, new Rgb24(25, 25, 28), 1.0));
        AssertDecodes(tmp, Directory.EnumerateFiles(capDir), content);
    }

    // ---------- Corruption must be detected, never silently accepted ----------

    [Fact]
    public void CorruptedDataArea_WithoutEcc_FailsPayloadCrc()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, opt: NoEcc);
        string bad = Capture(files[0], tmp.File("bad.png"), img =>
        {
            FillRect(img, new Rectangle(img.Width / 2, img.Height / 2, 24, 24), new Rgb24(1, 254, 3));
            return img.Clone();
        });
        var ex = Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(bad));
        Assert.Contains("CRC-32 mismatch", ex.Message);
    }

    [Fact]
    public void BothMetadataStrips_Corrupted_IsRejected()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);
        string bad = Capture(files[0], tmp.File("bad.png"), img =>
        {
            FillRect(img, new Rectangle(
                Layout.Border, Layout.Border + layout.Gutter, layout.InnerW, layout.MetaH), new Rgb24(0, 0, 0));
            FillRect(img, new Rectangle(
                Layout.Border, Layout.Border + layout.InnerH - layout.Gutter - layout.MetaH, layout.InnerW, layout.MetaH), new Rgb24(0, 0, 0));
            return img.Clone();
        });
        var ex = Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(bad));
        Assert.Contains("metadata strip is unreadable", ex.Message);
    }

    [Fact]
    public void DestroyedFrame_IsReported()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp);
        string bad = Capture(files[0], tmp.File("bad.png"), img =>
        {
            // Erase the top frame edge; the ring no longer covers its bounding box.
            FillRect(img, new Rectangle(0, 0, img.Width, Layout.Border), new Rgb24(255, 255, 255));
            return img.Clone();
        });
        var ex = Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(bad));
        Assert.Contains("frame", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonShardImage_IsRejected()
    {
        using var tmp = new TempDir();
        string plain = tmp.File("plain.png");
        using (var img = new Image<Rgb24>(400, 400, new Rgb24(200, 200, 200)))
            img.SaveAsPng(plain);
        Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(plain));
    }

    // ---------- Shard set handling ----------

    [Fact]
    public void MissingShard_ReportsWhichImageToRecapture()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, size: 150_000);
        Assert.True(files.Count >= 3);
        var partial = files.Where((_, i) => i != 1); // drop part 2

        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(partial, tmp.File("out.bin"), _ => { }));
        Assert.Contains("missing image(s) 2", ex.Message);
    }

    [Fact]
    public void DuplicateCaptures_AreHarmless()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, size: 100_000);
        AssertDecodes(tmp, files.Concat(files), content); // every shard captured twice
    }

    [Fact]
    public void CorruptShardAmongGoodOnes_FailsAsMissing_NotAsGarbage()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp, size: 150_000, opt: NoEcc);
        Capture(files[0], files[0], img =>
        {
            FillRect(img, new Rectangle(img.Width / 2, img.Height / 2, 24, 24), new Rgb24(1, 254, 3));
            return img.Clone();
        });

        var messages = new List<string>();
        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(files, tmp.File("out.bin"), messages.Add));
        Assert.Contains("missing image(s) 1", ex.Message);
        Assert.Contains(messages, m => m.Contains("FAILED") && m.Contains("CRC-32"));
    }

    [Fact]
    public void TwoDifferentFiles_InOneFolder_BothRestore()
    {
        using var tmp = new TempDir();
        byte[] contentA = TestData.Random(20_000, seed: 1);
        byte[] contentB = TestData.Random(20_000, seed: 2);
        string shardDir = tmp.Sub("shards");
        new ShardEncoder().Encode(tmp.WriteFile("a.bin", contentA), shardDir, Fast);
        new ShardEncoder().Encode(tmp.WriteFile("b.bin", contentB), shardDir, Fast);

        string cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = tmp.Sub("out");
        try
        {
            var restored = new ShardDecoder().DecodeFolder(Directory.EnumerateFiles(shardDir, "*.png"), null, _ => { });
            Assert.Equal(2, restored.Count);
            Assert.Equal(contentA, File.ReadAllBytes(restored.Single(r => r.FileName == "a.bin").OutputPath));
            Assert.Equal(contentB, File.ReadAllBytes(restored.Single(r => r.FileName == "b.bin").OutputPath));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    [Fact]
    public void TwoDifferentFiles_WithExplicitOutput_IsRejected()
    {
        using var tmp = new TempDir();
        string shardDir = tmp.Sub("shards");
        new ShardEncoder().Encode(tmp.WriteFile("a.bin", TestData.Random(5_000, 1)), shardDir, Fast);
        new ShardEncoder().Encode(tmp.WriteFile("b.bin", TestData.Random(5_000, 2)), shardDir, Fast);

        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ShardDecoder().DecodeFolder(Directory.EnumerateFiles(shardDir, "*.png"), tmp.File("out.bin"), _ => { }));
        Assert.Contains("multiple different files", ex.Message);
    }

    [Fact]
    public void ExistingOutputFile_IsNotOverwritten()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, size: 5_000);

        string cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = tmp.Sub("out");
        try
        {
            File.WriteAllBytes("input.bin", [1, 2, 3]); // pre-existing file with the original's name
            var restored = new ShardDecoder().DecodeFolder(files, null, _ => { });
            Assert.EndsWith("input.restored.bin", restored[0].OutputPath);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes("input.bin"));
            Assert.Equal(content, File.ReadAllBytes(restored[0].OutputPath));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }
}
