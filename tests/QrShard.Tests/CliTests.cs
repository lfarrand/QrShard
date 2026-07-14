using QrShard;

namespace QrShard.Tests;

public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void NoArguments_PrintsHelp()
    {
        var (code, output, _) = Run();
        Assert.Equal(0, code);
        Assert.Contains("usage:", output);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public void HelpFlags_PrintHelp(string flag)
    {
        var (code, output, _) = Run(flag);
        Assert.Equal(0, code);
        Assert.Contains("usage:", output);
    }

    [Fact]
    public void UnknownCommand_ReturnsUsageError()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("unknown command", err);
    }

    [Fact]
    public void Encode_MissingFile_ReturnsUsageError()
    {
        var (code, _, err) = Run("encode", @"Z:\does\not\exist.bin");
        Assert.Equal(2, code);
        Assert.Contains("file not found", err);
    }

    [Fact]
    public void Encode_NoFile_ReturnsUsageError()
    {
        var (code, _, err) = Run("encode");
        Assert.Equal(2, code);
        Assert.Contains("exactly one input file", err);
    }

    [Fact]
    public void Decode_MissingPath_ReturnsUsageError()
    {
        var (code, _, err) = Run("decode", @"Z:\nope");
        Assert.Equal(2, code);
        Assert.Contains("not found", err);
    }

    [Fact]
    public void Decode_FolderWithoutImages_ReturnsUsageError()
    {
        using var tmp = new TempDir();
        var (code, _, err) = Run("decode", tmp.Path);
        Assert.Equal(2, code);
        Assert.Contains("no image files", err);
    }

    [Fact]
    public void Encode_InvalidEccValue_ReportsError()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("f.bin", TestData.Random(100));
        var (code, _, err) = Run("encode", input, "-e", "15", "-r", "900");
        Assert.Equal(1, code);
        Assert.Contains("parity", err);
    }

    [Fact]
    public void Encode_MalformedResolution_ReportsError()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("f.bin", TestData.Random(100));
        var (code, _, err) = Run("encode", input, "-r", "banana");
        Assert.Equal(1, code);
        Assert.Contains("error:", err);
    }

    [Theory]
    [InlineData("2160", 2160, 2160)]
    [InlineData("3840x2160", 3840, 2160)]
    [InlineData("800X600", 800, 600)]
    public void ParseResolution_AcceptsSquareAndWxH(string value, int width, int height) =>
        Assert.Equal((width, height), Cli.ParseResolution(value));

    [Fact]
    public void EncodeDecodeInfo_EndToEnd()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("payload.bin", content);
        string shardDir = tmp.File("out-shards");

        var (encodeCode, encodeOut, _) = Run("encode", input, "-o", shardDir, "-r", "900", "-e", "16");
        Assert.Equal(0, encodeCode);
        Assert.Contains("Done:", encodeOut);
        Assert.NotEmpty(Directory.GetFiles(shardDir, "*.png"));

        string restored = tmp.File("restored.bin");
        var (decodeCode, decodeOut, _) = Run("decode", shardDir, "-o", restored);
        Assert.Equal(0, decodeCode);
        Assert.Contains("SHA-256 verified", decodeOut);
        Assert.Equal(content, File.ReadAllBytes(restored));

        var (infoCode, infoOut, _) = Run("info", Directory.GetFiles(shardDir, "*.png")[0]);
        Assert.Equal(0, infoCode);
        Assert.Contains("payload.bin", infoOut);
        Assert.Contains("RS parity 16", infoOut);
        Assert.Contains("CRC-32 verified", infoOut);
    }

    [Fact]
    public void Encode_NonSquareResolutionAndNoCompress_Works()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("wide.bin", TestData.CompressibleText(10_000));
        string shardDir = tmp.File("shards");
        var (code, output, _) = Run("encode", input, "-o", shardDir, "-r", "1200x800", "--no-compress");
        Assert.Equal(0, code);
        Assert.Contains("compression off", output);

        var shard = Decoder.DecodeImage(Directory.GetFiles(shardDir, "*.png")[0]);
        Assert.Equal(0, shard.Header.Flags & ShardHeader.FlagCompressed);
    }

    [Fact]
    public void Info_MissingArgument_ReturnsUsageError()
    {
        var (code, _, err) = Run("info");
        Assert.Equal(2, code);
        Assert.Contains("info requires", err);
    }

    [Fact]
    public void Encode_WithRecovery_ReportsParityAndSurvivesImageLoss()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(200_000);
        string input = tmp.WriteFile("payload.bin", content);
        string shardDir = tmp.File("shards");

        var (code, output, _) = Run("encode", input, "-o", shardDir, "-r", "900", "-R", "25");
        Assert.Equal(0, code);
        Assert.Contains("parity image(s)", output);
        Assert.Contains("can recover up to", output);

        // Delete a data image, then decode — the CLI must rebuild it from parity.
        string firstData = Directory.GetFiles(shardDir, "*.png").First(f => !f.Contains("parity"));
        File.Delete(firstData);

        string restored = tmp.File("restored.bin");
        var (decodeCode, decodeOut, _) = Run("decode", shardDir, "-o", restored);
        Assert.Equal(0, decodeCode);
        Assert.Contains("SHA-256 verified", decodeOut);
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void Info_OnParityImage_ShowsRecoveryLine()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("f.bin", TestData.Random(150_000));
        string shardDir = tmp.File("shards");
        Assert.Equal(0, Run("encode", input, "-o", shardDir, "-r", "900", "-R", "20").Code);

        string parity = Directory.GetFiles(shardDir, "*.png").First(f => f.Contains("parity"));
        var (code, output, _) = Run("info", parity);
        Assert.Equal(0, code);
        Assert.Contains("parity #", output);
        Assert.Contains("recovery  :", output);
    }

    [Fact]
    public void Encode_InvalidRecoveryValue_ReportsError()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("f.bin", TestData.Random(100));
        var (code, _, err) = Run("encode", input, "-r", "900", "-R", "500");
        Assert.Equal(1, code);
        Assert.Contains("Recovery percent", err);
    }

    [Fact]
    public void Decode_SingleImagePassedDirectly_Works()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(5_000);
        string input = tmp.WriteFile("one.bin", content);
        string shardDir = tmp.File("shards");
        Assert.Equal(0, Run("encode", input, "-o", shardDir, "-r", "900").Code);

        string restored = tmp.File("r.bin");
        var (code, _, _) = Run("decode", Directory.GetFiles(shardDir, "*.png")[0], "-o", restored);
        Assert.Equal(0, code);
        Assert.Equal(content, File.ReadAllBytes(restored));
    }
}
