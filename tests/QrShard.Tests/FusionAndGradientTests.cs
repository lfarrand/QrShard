using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Multi-capture fusion of individually-failed photos and vertical-gradient palette tracking.</summary>
public class FusionAndGradientTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    /// <summary>Copies a shard image and fills a square block with flat gray (unrecoverable locally).</summary>
    private static string DamageCopy(string source, string destPath, int x0, int y0, int size)
    {
        using var img = Image.Load<Rgb24>(source);
        for (int y = y0; y < y0 + size; y++)
            for (int x = x0; x < x0 + size; x++)
                img[x, y] = new Rgb24(128, 128, 128);
        img.SaveAsPng(destPath);
        return destPath;
    }

    [Fact]
    public void TwoFailedCaptures_WithDisjointDamage_FuseIntoValidShard()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.Equal(1, result.ImageCount);

        string capDir = tmp.Sub("captures");
        string cap1 = DamageCopy(result.Files[0], Path.Combine(capDir, "cap1.png"), 100, 100, 250);
        string cap2 = DamageCopy(result.Files[0], Path.Combine(capDir, "cap2.png"), 450, 450, 250);

        // Each capture must individually exceed ECC capacity...
        Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(cap1));
        Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(cap2));

        // ...but fusing them recovers the shard and the file round-trips.
        var log = new List<string>();
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([cap1, cap2], output, log.Add);
        Assert.Contains(log, m => m.Contains("fused"));
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void ThreeFailedCaptures_MajorityVote_RecoversOverlappingDamage()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        string capDir = tmp.Sub("captures");
        string cap1 = DamageCopy(result.Files[0], Path.Combine(capDir, "cap1.png"), 100, 100, 260);
        string cap2 = DamageCopy(result.Files[0], Path.Combine(capDir, "cap2.png"), 400, 150, 260);
        string cap3 = DamageCopy(result.Files[0], Path.Combine(capDir, "cap3.png"), 250, 500, 260);

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([cap1, cap2, cap3], output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void FusionResult_StillGatedByCrc_MixedShardsDoNotFuse()
    {
        using var tmp = new TempDir();
        // Two DIFFERENT shards (different files), each damaged beyond correction: same layout
        // signature, but fusing their cells must never produce a bogus "valid" shard.
        string inputA = tmp.WriteFile("a.bin", TestData.Random(20_000, 1));
        string inputB = tmp.WriteFile("b.bin", TestData.Random(20_000, 2));
        var resultA = new ShardEncoder().Encode(inputA, tmp.Sub("shardsA"), Fast);
        var resultB = new ShardEncoder().Encode(inputB, tmp.Sub("shardsB"), Fast);

        string capDir = tmp.Sub("captures");
        string capA = DamageCopy(resultA.Files[0], Path.Combine(capDir, "capA.png"), 100, 100, 300);
        string capB = DamageCopy(resultB.Files[0], Path.Combine(capDir, "capB.png"), 450, 450, 300);

        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder([capA, capB], tmp.File("out.bin"), _ => { }));
        Assert.Contains("No decodable shard images", ex.Message);
    }

    [Fact]
    public void VerticalIlluminationGradient_DecodesViaPaletteInterpolation()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(30_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        // Simulate strong vertical screen falloff: rows darken from full brightness at the
        // bottom to 60% at the top — the kind of gradient a photographed screen shows.
        string graded = tmp.File("graded.png");
        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            for (int y = 0; y < img.Height; y++)
            {
                double factor = 0.6 + 0.4 * y / (img.Height - 1);
                for (int x = 0; x < img.Width; x++)
                {
                    var p = img[x, y];
                    img[x, y] = new Rgb24((byte)(p.R * factor), (byte)(p.G * factor), (byte)(p.B * factor));
                }
            }
            img.SaveAsPng(graded);
        }

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([graded], output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void UniformCapture_DoesNotTriggerInterpolation()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(10_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        // Pixel-perfect capture: strips are identical, so the classic LUT path must be chosen.
        var scratch = new DecodeScratch();
        var decoder = new ShardDecoder();
        var shard = decoder.DecodeImage(result.Files[0], scratch);
        Assert.Equal(0, shard.CorrectedBytes);
    }
}
