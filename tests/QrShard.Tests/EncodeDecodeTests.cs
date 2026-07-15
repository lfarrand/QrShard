using System.Text.RegularExpressions;
using QrShard;

namespace QrShard.Tests;

/// <summary>End-to-end round trips over the real encoder and decoder (exact, unscaled captures).</summary>
public class EncodeDecodeTests
{
    // Small resolution keeps the suite fast; geometry code paths are identical at any size.
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static byte[] RoundTrip(TempDir tmp, byte[] content, EncodeOptions opt, string name = "input.bin")
    {
        string input = tmp.WriteFile(name, content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), opt);
        string output = tmp.File("restored.bin");
        var restored = Decoder.DecodeFolder(result.Files, output, _ => { });
        Assert.Single(restored);
        Assert.Equal(name, restored[0].FileName);
        return File.ReadAllBytes(output);
    }

    [Fact]
    public void SingleImage_RoundTripsByteIdentical()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(10_000);
        Assert.Equal(content, RoundTrip(tmp, content, Fast));
    }

    [Fact]
    public void MultiImage_RoundTripsByteIdentical()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("multi.bin", content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3, $"expected several images, got {result.ImageCount}");

        string output = tmp.File("restored.bin");
        Decoder.DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void EmptyFile_RoundTrips()
    {
        using var tmp = new TempDir();
        Assert.Equal([], RoundTrip(tmp, [], Fast));
    }

    [Fact]
    public void SingleByteFile_RoundTrips()
    {
        using var tmp = new TempDir();
        Assert.Equal(new byte[] { 0xA5 }, RoundTrip(tmp, [0xA5], Fast));
    }

    [Theory]
    [InlineData(1, 1)] // black & white, minimal density
    [InlineData(2, 2)]
    [InlineData(1, 8)] // 1px cells, 256 colors — maximum density (regression: half-pixel rounding)
    [InlineData(2, 6)]
    [InlineData(5, 5)] // odd bit widths exercise non-byte-aligned cell packing
    [InlineData(4, 3)]
    public void AllDensityConfigs_RoundTripByteIdentical(int cellPx, int bits)
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000, seed: cellPx * 10 + bits);
        var opt = new EncodeOptions { Width = 900, Height = 900, CellPx = cellPx, BitsPerCell = bits };
        Assert.Equal(content, RoundTrip(tmp, content, opt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(64)]
    public void EccParityLevels_RoundTripByteIdentical(int parity)
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(15_000, seed: parity);
        Assert.Equal(content, RoundTrip(tmp, content, Fast with { EccParity = parity }));
    }

    [Fact]
    public void NonSquareResolution_RoundTripsByteIdentical()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(30_000);
        var opt = new EncodeOptions { Width = 1400, Height = 800, CellPx = 3, BitsPerCell = 4 };
        Assert.Equal(content, RoundTrip(tmp, content, opt));
    }

    [Fact]
    public void CompressibleContent_SetsCompressedFlag_AndRoundTrips()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.CompressibleText(120_000);
        string input = tmp.WriteFile("text.txt", content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);

        // Highly repetitive text must deflate into a single image at this capacity.
        Assert.Equal(1, result.ImageCount);
        var shard = Decoder.DecodeImage(result.Files[0]);
        Assert.Equal(ShardHeader.FlagCompressed, (byte)(shard.Header.Flags & ShardHeader.FlagCompressed));
        Assert.Equal((long)content.Length, shard.Header.OriginalLength);
        Assert.True(shard.Header.TotalLength < content.Length);

        string output = tmp.File("restored.txt");
        Decoder.DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void IncompressibleContent_IsStoredRaw()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("noise.bin", TestData.Random(50_000));
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);
        var shard = Decoder.DecodeImage(result.Files[0]);
        Assert.Equal(0, shard.Header.Flags & ShardHeader.FlagCompressed);
        Assert.Equal(50_000L, shard.Header.TotalLength);
    }

    [Fact]
    public void CompressionDisabled_IsStoredRaw()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("text.txt", TestData.CompressibleText(30_000));
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast with { Compress = false });
        var shard = Decoder.DecodeImage(result.Files[0]);
        Assert.Equal(0, shard.Header.Flags & ShardHeader.FlagCompressed);
    }

    [Fact]
    public void LooksCompressible_SkipsLargeIncompressibleInput()
    {
        // > 4 MB of noise: the sampling heuristic must bail out instead of deflating it all.
        Assert.False(Encoder.LooksCompressible(new BytePayloadSource(TestData.Random(6_000_000))));
        Assert.True(Encoder.LooksCompressible(new BytePayloadSource(TestData.CompressibleText(6_000_000))));
        Assert.True(Encoder.LooksCompressible(new BytePayloadSource(TestData.Random(1_000)))); // small inputs always try
    }

    [Fact]
    public void UnicodeFileName_SurvivesRoundTrip()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(5_000);
        string input = tmp.WriteFile("файл-数据.bin", content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);
        var shard = Decoder.DecodeImage(result.Files[0]);
        Assert.Equal("файл-数据.bin", shard.Header.FileName);
    }

    [Fact]
    public void ShardFileNames_FollowNumberingPattern()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("doc.bin", TestData.Random(100_000));
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);
        foreach (string f in result.Files)
            Assert.Matches(new Regex(@"doc\.bin\.qrs\d{3,}of\d{3,}\.png$"), f);
    }

    [Fact]
    public void EncodeResult_CapacityMatchesLayoutMinusHeader()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("cap.bin", TestData.Random(1_000));
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);
        Assert.Equal(layout.UsableBytes - ShardHeader.Size("cap.bin"), result.BytesPerImage);
        Assert.Equal(layout.Width, result.Width);
        Assert.Equal(layout.Height, result.Height);
    }

    [Fact]
    public void ShardHeaders_CarryConsistentIndexCountAndPayloadSizes()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(100_000);
        string input = tmp.WriteFile("parts.bin", content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);

        var shards = result.Files.Select(Decoder.DecodeImage).OrderBy(s => s.Header.Index).ToList();
        Assert.Equal(result.ImageCount, shards.Count);
        Assert.All(shards, s => Assert.Equal(result.ImageCount, s.Header.Count));
        Assert.Equal(Enumerable.Range(0, shards.Count), shards.Select(s => s.Header.Index));
        Assert.True(shards.Select(s => s.Header.FileId).Distinct().Count() == 1);
        Assert.Equal((long)content.Length, shards.Sum(s => (long)s.Payload.Length)); // random data: stored raw
        Assert.All(shards, s => Assert.Equal(TestData.Sha256(content), s.Header.Sha256));
    }

    [Fact]
    public void DecodeFolder_IgnoresShardOrderAndFileNames()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000);
        string input = tmp.WriteFile("orig.bin", content);
        var result = Encoder.Encode(input, tmp.Sub("shards"), Fast);

        // Rename captures to arbitrary names in scrambled order — headers alone must suffice.
        string captureDir = tmp.Sub("captures");
        var rng = new Random(7);
        foreach (var (f, i) in result.Files.OrderBy(_ => rng.Next()).Select((f, i) => (f, i)))
            File.Copy(f, Path.Combine(captureDir, $"screenshot-{i}.png"));

        string output = tmp.File("restored.bin");
        Decoder.DecodeFolder(Directory.EnumerateFiles(captureDir), output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
