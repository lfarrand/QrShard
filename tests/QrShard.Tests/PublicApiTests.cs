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

    private static readonly QrShardEncodeOptions Compact = new() { Width = 700, Height = 700 };

    [Theory]
    [InlineData(nameof(QrShardEncodeOptions.CellPx))]
    [InlineData(nameof(QrShardEncodeOptions.BitsPerCell))]
    [InlineData(nameof(QrShardEncodeOptions.EccParity))]
    [InlineData(nameof(QrShardEncodeOptions.FountainPercent))]
    [InlineData(nameof(QrShardEncodeOptions.CameraMode))]
    [InlineData(nameof(QrShardEncodeOptions.Compress))]
    [InlineData(nameof(QrShardEncodeOptions.Interleave2))]
    public void EncodeFile_AppliesEachMappedOption(string field)
    {
        using var tmp = new TempDir();
        byte[] content = field == nameof(QrShardEncodeOptions.Compress)
            ? TestData.CompressibleText(256)
            : TestData.Random(80);
        string input = tmp.WriteFile("input.bin", content);

        QrShardEncodeOptions options = field switch
        {
            nameof(QrShardEncodeOptions.CellPx) => Compact with { CellPx = 8 },
            nameof(QrShardEncodeOptions.BitsPerCell) => Compact with { BitsPerCell = 2 },
            nameof(QrShardEncodeOptions.EccParity) => Compact with { EccParity = 32 },
            nameof(QrShardEncodeOptions.FountainPercent) => Compact with { FountainPercent = 100 },
            nameof(QrShardEncodeOptions.CameraMode) => Compact with { CameraMode = true },
            nameof(QrShardEncodeOptions.Compress) => Compact with { Compress = false },
            nameof(QrShardEncodeOptions.Interleave2) => Compact with { Interleave2 = true },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown mapped field."),
        };

        var codec = new QrShardCodec();
        var report = codec.EncodeFile(input, tmp.Sub("shards"), options);

        var diag = new ShardDecoder().Diagnose(report.Files[0]);
        Assert.NotNull(diag.Layout);
        Assert.NotNull(diag.Shard);
        Layout layout = diag.Layout;
        ShardHeader header = diag.Shard.Header;

        switch (field)
        {
            case nameof(QrShardEncodeOptions.CellPx):
                Assert.Equal(8, layout.CellPx);
                break;
            case nameof(QrShardEncodeOptions.BitsPerCell):
                Assert.Equal(2, layout.BitsPerCell);
                break;
            case nameof(QrShardEncodeOptions.EccParity):
                Assert.Equal(32, layout.EccParity);
                Assert.Equal(32, diag.Shard.EccParity);
                break;
            case nameof(QrShardEncodeOptions.FountainPercent):
                Assert.True(report.ParityImages >= 1);
                Assert.Equal(ShardHeader.FlagFountain, header.Flags & ShardHeader.FlagFountain);
                Assert.True(header.StripeParity > 0);
                break;
            case nameof(QrShardEncodeOptions.CameraMode):
                var camera = Layout.Create(Compact.Width, Compact.Height, Compact.CellPx,
                    Compact.BitsPerCell, Compact.EccParity, cameraFinders: true);
                var screenshot = Layout.Create(Compact.Width, Compact.Height, Compact.CellPx,
                    Compact.BitsPerCell, Compact.EccParity);
                Assert.Equal(camera.GridH, layout.GridH);
                Assert.True(layout.GridH < screenshot.GridH);
                Assert.Equal(camera.Height, report.Height);
                break;
            case nameof(QrShardEncodeOptions.Compress):
                Assert.Equal(0, header.Flags & ShardHeader.FlagCompressed);
                break;
            case nameof(QrShardEncodeOptions.Interleave2):
                Assert.True(layout.Interleave2);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown mapped field.");
        }

        string output = tmp.File("out.bin");
        IReadOnlyList<QrShardDecodedFile> restored = codec.DecodeImages(report.Files, output);
        Assert.Single(restored);
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
