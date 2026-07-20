using QrShard;

namespace QrShard.Tests;

/// <summary>Payload-format features: Brotli compression, AES-GCM encryption, folder archives.</summary>
public class PayloadFeatureTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    [Fact]
    public void CompressedPayloads_UseBrotli()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("text.txt", TestData.CompressibleText(50_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        var shard = new ShardDecoder().DecodeImage(result.Files[0]);
        Assert.Equal(ShardHeader.FlagCompressed, (byte)(shard.Header.Flags & ShardHeader.FlagCompressed));
        Assert.Equal(ShardHeader.FlagBrotli, (byte)(shard.Header.Flags & ShardHeader.FlagBrotli));
    }

    [Fact]
    public void Encrypted_RoundTrips_WithPassword()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("secret.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { Password = "hunter2" });

        var shard = new ShardDecoder().DecodeImage(result.Files[0]);
        Assert.Equal(ShardHeader.FlagEncrypted, (byte)(shard.Header.Flags & ShardHeader.FlagEncrypted));

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { }, "hunter2");
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Encrypted_CompressibleContent_CompressesThenEncrypts()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.CompressibleText(120_000);
        string input = tmp.WriteFile("secret.txt", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { Password = "pw" });

        // Compression must still win (the ciphertext of compressed text is far smaller than the input).
        var shard = new ShardDecoder().DecodeImage(result.Files[0]);
        Assert.Equal(ShardHeader.FlagCompressed, (byte)(shard.Header.Flags & ShardHeader.FlagCompressed));
        Assert.Equal(ShardHeader.FlagEncrypted, (byte)(shard.Header.Flags & ShardHeader.FlagEncrypted));
        Assert.True(shard.Header.TotalLength < content.Length);

        string output = tmp.File("out.txt");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { }, "pw");
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Encrypted_MissingPassword_FailsWithClearError()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("secret.bin", TestData.Random(5_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { Password = "pw" });

        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(result.Files, tmp.File("out.bin"), _ => { }));
        Assert.Contains("encrypted", ex.Message);
    }

    [Fact]
    public void Encrypted_WrongPassword_FailsWithClearError()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("secret.bin", TestData.Random(5_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { Password = "right" });

        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(result.Files, tmp.File("out.bin"), _ => { }, "wrong"));
        Assert.Contains("wrong password", ex.Message);
    }

    [Fact]
    public void EncryptedEmptyFile_RoundTrips()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("empty.bin", []);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { Password = "pw" });
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { }, "pw");
        Assert.Equal(Array.Empty<byte>(), File.ReadAllBytes(output));
    }

    [Fact]
    public void Encrypted_WithParityRecovery_SurvivesImageLoss()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("secret.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            Fast with { Password = "pw", RecoveryPercent = 25 });
        Assert.True(result.ParityImages >= 1);

        var survivors = result.Files.Where((_, i) => i != 0).ToList(); // drop one data image
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(survivors, output, _ => { }, "pw");
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void FolderEncode_ExtractsOnDecode()
    {
        using var tmp = new TempDir();
        byte[] contentA = TestData.Random(30_000, 1);
        byte[] contentB = TestData.Random(8_000, 2);
        string dir = tmp.Sub("project");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), contentA);
        File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), contentB);

        string shardDir = tmp.File("shards");
        int code = new Cli().Run(["encode", dir, "-o", shardDir, "-r", "900"], new StringWriter(), new StringWriter());
        Assert.Equal(0, code);

        string destDir = tmp.File("restored");
        var stdout = new StringWriter();
        code = new Cli().Run(["decode", shardDir, "-o", destDir], stdout, new StringWriter());
        Assert.Equal(0, code);
        Assert.Contains("extracted", stdout.ToString());
        Assert.Equal(contentA, File.ReadAllBytes(Path.Combine(destDir, "a.bin")));
        Assert.Equal(contentB, File.ReadAllBytes(Path.Combine(destDir, "sub", "b.bin")));
    }
}
