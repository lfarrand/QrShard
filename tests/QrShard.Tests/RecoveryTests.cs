using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// End-to-end cross-shard recovery: whole images missing or destroyed must be rebuilt from
/// parity images without recapture. Complements per-image ECC (DamageRecoveryTests).
/// </summary>
public class RecoveryTests
{
    private static EncodeOptions Opt(int recovery) =>
        new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4, RecoveryPercent = recovery };

    private static (byte[] Content, EncodeResult Result) Encode(TempDir tmp, int size, int recovery, int seed = 3)
    {
        byte[] content = TestData.Random(size, seed);
        string input = tmp.WriteFile("input.bin", content);
        return (content, Encoder.Encode(input, tmp.Sub("shards"), Opt(recovery)));
    }

    private static byte[] Decode(TempDir tmp, IEnumerable<string> images)
    {
        string output = tmp.File($"out-{Guid.NewGuid().ToString("N")[..8]}.bin");
        Decoder.DecodeFolder(images, output, _ => { });
        return File.ReadAllBytes(output);
    }

    [Fact]
    public void Encode_ProducesParityImages()
    {
        using var tmp = new TempDir();
        var (_, result) = Encode(tmp, 200_000, recovery: 20);
        Assert.True(result.DataImages >= 5);
        Assert.True(result.ParityImages > 0);
        Assert.Equal(result.DataImages + result.ParityImages, result.Files.Count);
        Assert.True(result.StripeParity >= 1);
    }

    [Fact]
    public void ZeroRecovery_ProducesNoParity()
    {
        using var tmp = new TempDir();
        var (_, result) = Encode(tmp, 100_000, recovery: 0);
        Assert.Equal(0, result.ParityImages);
        Assert.Equal(result.DataImages, result.Files.Count);
    }

    [Fact]
    public void NoLoss_WithParity_RoundTrips()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 300_000, recovery: 15);
        Assert.Equal(content, Decode(tmp, result.Files));
    }

    [Fact]
    public void MissingDataImages_AreRebuiltFromParity()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 300_000, recovery: 25);
        var data = result.Files.Where(f => !f.Contains("parity")).ToList();

        // Delete two whole data images; keep all parity.
        File.Delete(data[0]);
        File.Delete(data[data.Count / 2]);
        var remaining = result.Files.Where(File.Exists);
        Assert.Equal(content, Decode(tmp, remaining));
    }

    [Fact]
    public void MixedDataAndParityLoss_UpToBudget_IsRecovered()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 250_000, recovery: 30);
        int budget = result.StripeParity;
        Assert.True(budget >= 2, "test needs a parity budget of at least 2");

        var data = result.Files.Where(f => !f.Contains("parity")).ToList();
        var parity = result.Files.Where(f => f.Contains("parity")).ToList();

        // Lose (budget-1) data images and 1 parity image — total loss within budget per stripe.
        for (int i = 0; i < budget - 1; i++)
            File.Delete(data[i]);
        File.Delete(parity[0]);

        Assert.Equal(content, Decode(tmp, result.Files.Where(File.Exists)));
    }

    [Fact]
    public void LossBeyondParity_FailsWithClearMessage()
    {
        using var tmp = new TempDir();
        var (_, result) = Encode(tmp, 120_000, recovery: 10);
        var data = result.Files.Where(f => !f.Contains("parity")).ToList();

        // Delete more data images than the per-stripe parity budget can cover.
        int toDelete = result.StripeParity + 1;
        foreach (string f in data.Take(toDelete))
            File.Delete(f);

        var ex = Assert.Throws<ShardDecodeException>(
            () => Decode(tmp, result.Files.Where(File.Exists)));
        Assert.Contains("beyond parity recovery", ex.Message);
    }

    [Fact]
    public void DestroyedImage_TreatedAsMissing_AndRecovered()
    {
        // An image damaged past its per-image ECC is unreadable; parity must still rebuild it.
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 200_000, recovery: 25);
        var data = result.Files.Where(f => !f.Contains("parity")).ToList();

        // Obliterate the frame of one data image so it cannot be decoded at all.
        using (var img = Image.Load<Rgb24>(data[1]))
        {
            img.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < img.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < img.Width; x++)
                        row[x] = new Rgb24(128, 128, 128);
                }
            });
            img.Save(data[1]);
        }

        Assert.Equal(content, Decode(tmp, result.Files));
    }

    [Fact]
    public void SingleImageFile_WithRecovery_RoundTrips()
    {
        // Small file that fits one data image, plus parity.
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 5_000, recovery: 50);
        Assert.Equal(1, result.DataImages);
        Assert.True(result.ParityImages >= 1);

        // Lose the single data image; rebuild from parity.
        File.Delete(result.Files.First(f => !f.Contains("parity")));
        Assert.Equal(content, Decode(tmp, result.Files.Where(File.Exists)));
    }

    [Fact]
    public void RecoveredFile_PassesShaVerification()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 400_000, recovery: 20);
        var data = result.Files.Where(f => !f.Contains("parity")).ToList();
        File.Delete(data[2]);

        var messages = new List<string>();
        string output = tmp.File("verified.bin");
        Decoder.DecodeFolder(result.Files.Where(File.Exists), output, messages.Add);
        Assert.Contains(messages, m => m.Contains("recovered") && m.Contains("from parity"));
        Assert.Contains(messages, m => m.Contains("SHA-256 verified"));
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    public void VariousRecoveryLevels_RoundTripAfterMaxLoss(int recovery)
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 300_000, recovery, seed: recovery);
        var data = result.Files.Where(f => !f.Contains("parity")).ToList();

        // Remove exactly the per-stripe parity budget worth of the first data images.
        foreach (string f in data.Take(result.StripeParity))
            File.Delete(f);

        Assert.Equal(content, Decode(tmp, result.Files.Where(File.Exists)));
    }

    [Fact]
    public void PlanStripes_KeepsWithinGaloisLimit()
    {
        foreach (int recovery in new[] { 1, 10, 25, 50, 100 })
        {
            var (s, p) = Encoder.PlanStripes(10_000, recovery);
            Assert.True(s + p <= CrossShardFec.MaxShardsPerStripe, $"recovery {recovery}: {s}+{p}");
            Assert.True(s >= 1 && p >= 1);
        }
    }

    [Fact]
    public void PlanStripes_ZeroRecovery_IsDisabled() =>
        Assert.Equal((0, 0), Encoder.PlanStripes(100, 0));
}
