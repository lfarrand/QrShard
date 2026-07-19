using QrShard;

namespace QrShard.Tests;

/// <summary>The memory-mapped streaming payload source and its integration into the encoder.</summary>
public class StreamingTests
{
    [Fact]
    public void MappedSource_ReadsMatchFileContent()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(100_000, seed: 31);
        string path = tmp.WriteFile("data.bin", content);

        using var source = new MappedPayloadSource(path);
        Assert.Equal(content.LongLength, source.Length);

        var chunk = new byte[10_000];
        source.Read(0, chunk);
        Assert.Equal(content[..10_000], chunk);
        source.Read(90_000, chunk);
        Assert.Equal(content[90_000..], chunk);
        source.Read(12_345, chunk.AsSpan(0, 100));
        Assert.Equal(content[12_345..12_445], chunk[..100]);
    }

    [Fact]
    public void MappedSource_ConcurrentReads_AreConsistent()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(1_000_000, seed: 32);
        string path = tmp.WriteFile("data.bin", content);

        using var source = new MappedPayloadSource(path);
        Parallel.For(0, 64, i =>
        {
            var chunk = new byte[10_000];
            long offset = i * 15_000L;
            source.Read(offset, chunk);
            Assert.Equal(content.AsSpan((int)offset, 10_000).ToArray(), chunk);
        });
    }

    [Fact]
    public void MappedSource_OutOfRangeRead_Throws()
    {
        using var tmp = new TempDir();
        string path = tmp.WriteFile("data.bin", TestData.Random(1_000));
        using var source = new MappedPayloadSource(path);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(900, new byte[200]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(-1, new byte[10]));
    }

    [Fact]
    public void ComputeSha256_MatchesOneShotHash()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(9_000_000, seed: 33); // > one 4 MB hashing chunk
        string path = tmp.WriteFile("data.bin", content);
        using var source = new MappedPayloadSource(path);
        Assert.Equal(TestData.Sha256(content), PayloadSource.ComputeSha256(source));
    }

    [Fact]
    public void LargeIncompressibleFile_StreamsAndRoundTrips()
    {
        // > 4 MB of noise takes the memory-mapped (non-materialized) encode path.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(6_000_000, seed: 34);
        string input = tmp.WriteFile("big.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), new EncodeOptions());

        string output = tmp.File("restored.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void NoCompressFlag_StreamsAndRoundTrips()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.CompressibleText(200_000);
        string input = tmp.WriteFile("text.txt", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, Compress = false });

        var shard = new ShardDecoder().DecodeImage(result.Files[0]);
        Assert.Equal(0, shard.Header.Flags & ShardHeader.FlagCompressed);

        string output = tmp.File("restored.txt");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void StreamedEncode_WithRecoveryParity_SurvivesImageLoss()
    {
        // Cross-shard parity chunks are read through the streaming source too.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(6_000_000, seed: 35);
        string input = tmp.WriteFile("big.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { RecoveryPercent = 20 });
        Assert.True(result.ParityImages >= 1);

        File.Delete(result.Files.First(f => !f.Contains("parity")));
        string output = tmp.File("restored.bin");
        new ShardDecoder().DecodeFolder(result.Files.Where(File.Exists), output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
