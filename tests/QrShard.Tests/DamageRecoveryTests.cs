using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// End-to-end damage scenarios: things that land on real screenshots (cursors, notification
/// banners, JPEG re-encoding, dead strips) and must be absorbed by ECC and strip redundancy.
/// </summary>
public class DamageRecoveryTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (byte[] Content, List<string> Files) Encode(TempDir tmp, EncodeOptions? opt = null, int size = 25_000)
    {
        byte[] content = TestData.Random(size, seed: 77);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), opt ?? Fast);
        return (content, result.Files);
    }

    private static string Damage(string srcPath, string dstPath, Action<Image<Rgb24>> mutate)
    {
        using var img = Image.Load<Rgb24>(srcPath);
        mutate(img);
        img.SaveAsPng(dstPath);
        return dstPath;
    }

    private static void FillRect(Image<Rgb24> img, Rectangle r, Rgb24 color)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int y = Math.Max(0, r.Top); y < Math.Min(img.Height, r.Bottom); y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = Math.Max(0, r.Left); x < Math.Min(img.Width, r.Right); x++)
                    row[x] = color;
            }
        });
    }

    [Fact]
    public void CursorSizedBlob_IsCorrectedByEcc()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
            FillRect(img, new Rectangle(img.Width / 2 + 60, img.Height / 2, 22, 30), new Rgb24(255, 255, 255)));

        var shard = new ShardDecoder().DecodeImage(captured);
        Assert.True(shard.CorrectedBytes > 0, "expected ECC corrections to be reported");
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void NotificationBanner_AcrossDataArea_IsCorrected()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
            FillRect(img, new Rectangle(120, 200, 150, 40), new Rgb24(50, 120, 220)));

        var shard = new ShardDecoder().DecodeImage(captured);
        Assert.True(shard.CorrectedBytes > 0);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void ExcessiveDamage_FailsCleanly_NotSilently()
    {
        using var tmp = new TempDir();
        var (_, files) = Encode(tmp);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
            FillRect(img, new Rectangle(150, 150, 400, 400), new Rgb24(128, 128, 128)));

        var ex = Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(captured));
        Assert.Contains("ecapture", ex.Message); // "Recapture it." / "Recapture this image."
    }

    [Fact]
    public void TopStripsDestroyed_BottomCopiesTakeOver()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
        {
            // Obliterate the top metadata AND palette strips.
            FillRect(img, new Rectangle(
                Layout.Border, Layout.Border + layout.Gutter, layout.InnerW, 2 * layout.MetaH), new Rgb24(10, 10, 10));
        });

        var shard = new ShardDecoder().DecodeImage(captured);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void BottomStripsDestroyed_TopCopiesUsed()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
        {
            FillRect(img, new Rectangle(
                Layout.Border, Layout.Border + layout.InnerH - layout.Gutter - 2 * layout.MetaH,
                layout.InnerW, 2 * layout.MetaH), new Rgb24(240, 200, 40));
        });

        var shard = new ShardDecoder().DecodeImage(captured);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void JpegRecodedCapture_IsCorrectedByEcc()
    {
        // Some capture tools re-encode screenshots as JPEG; high-quality JPEG artifacts must be
        // absorbed by ECC. (Quality >= ~91 avoids chroma subsampling in ImageSharp.)
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp, Fast with { EccParity = 32 });
        string jpegPath = tmp.File("cap.jpg");
        using (var img = Image.Load<Rgb24>(files[0]))
            img.SaveAsJpeg(jpegPath, new JpegEncoder { Quality = 95 });

        var shard = new ShardDecoder().DecodeImage(jpegPath);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void MultipleSmallDefects_AcrossTheImage_AreCorrected()
    {
        using var tmp = new TempDir();
        var (content, files) = Encode(tmp);
        string captured = Damage(files[0], tmp.File("cap.png"), img =>
        {
            var rng = new Random(99);
            for (int i = 0; i < 12; i++)
            {
                FillRect(img, new Rectangle(
                    rng.Next(Layout.Border + 30, img.Width - 60),
                    rng.Next(img.Height / 3, img.Height - 120),
                    rng.Next(4, 12), rng.Next(4, 12)), new Rgb24(255, 128, 0));
            }
        });

        var shard = new ShardDecoder().DecodeImage(captured);
        Assert.True(shard.CorrectedBytes > 0);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Fact]
    public void FullPipeline_WithDamagedCaptures_RestoresFile()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(100_000, seed: 5);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.Files.Count >= 2);

        string capDir = tmp.Sub("captures");
        var rng = new Random(6);
        foreach (string f in result.Files)
        {
            Damage(f, Path.Combine(capDir, Path.GetFileName(f)), img =>
                FillRect(img, new Rectangle(
                    rng.Next(100, img.Width - 140), rng.Next(150, img.Height - 190), 25, 25),
                    new Rgb24(255, 255, 255)));
        }

        string output = tmp.File("restored.bin");
        new ShardDecoder().DecodeFolder(Directory.EnumerateFiles(capDir), output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
