using QrShard;

namespace QrShard.Tests;

/// <summary>The embeddable public API (QrShard.Core's QrShardCodec facade).</summary>
public class PublicApiTests
{
    [Fact]
    public void Codec_RoundTrips_ThroughThePublicSurfaceOnly()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000);
        string input = tmp.WriteFile("input.bin", content);

        var codec = new QrShardCodec();
        var report = codec.EncodeFile(input, tmp.Sub("shards"), new QrShardEncodeOptions
        {
            Width = 900,
            Height = 900,
            RecoveryPercent = 25,
        });
        Assert.True(report.ImageCount >= 2);
        Assert.True(report.ParityImages >= 1);
        Assert.All(report.Files, f => Assert.True(File.Exists(f)));

        // Lose an image; the public decode must recover and verify.
        var survivors = report.Files.Where((_, i) => i != 0).ToList();
        string output = tmp.File("out.bin");
        var progress = new List<string>();
        var restored = codec.DecodeImages(survivors, output, progress: progress.Add);

        Assert.Single(restored);
        Assert.Equal("input.bin", restored[0].FileName);
        Assert.Equal(content.Length, restored[0].Length);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.Contains(progress, m => m.Contains("SHA-256 verified"));
    }

    [Fact]
    public void Codec_EncryptedRoundTrip_AndTypedDecodeFailure()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(10_000);
        string input = tmp.WriteFile("secret.bin", content);

        var codec = new QrShardCodec();
        var report = codec.EncodeFile(input, tmp.Sub("shards"), new QrShardEncodeOptions
        {
            Width = 900,
            Height = 900,
            Password = "hunter2",
        });

        var ex = Assert.Throws<QrShardDecodeException>(
            () => codec.DecodeImages(report.Files, tmp.File("out.bin"), password: "wrong"));
        Assert.Contains("wrong password", ex.Message);

        string output = tmp.File("out.bin");
        codec.DecodeImages(report.Files, output, password: "hunter2");
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Codec_InvalidOptions_ThrowArgumentException()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(100));
        Assert.Throws<ArgumentException>(() => new QrShardCodec().EncodeFile(
            input, tmp.Sub("shards"), new QrShardEncodeOptions { RecoveryPercent = 10, FountainPercent = 10 }));
    }
}
