using System.Text.Json;
using QrShard;

namespace QrShard.Tests;

/// <summary>Machine-readable output and the forward-compatibility flag guard.</summary>
public class JsonAndGuardTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (int Code, string Out) Run(params string[] args)
    {
        var stdout = new StringWriter();
        int code = new Cli().Run(args, stdout, new StringWriter());
        return (code, stdout.ToString());
    }

    [Fact]
    public void Verify_Json_IsParseableAndAccurate()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        File.Delete(result.Files[1]);

        var (code, output) = Run("verify", tmp.File("shards"), "--json");
        Assert.Equal(1, code);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());
        var file = doc.RootElement.GetProperty("files")[0];
        Assert.Equal("input.bin", file.GetProperty("fileName").GetString());
        Assert.Equal(result.ImageCount, file.GetProperty("dataTotal").GetInt32());
        Assert.Equal(2, file.GetProperty("missing")[0].GetInt32());
    }

    [Fact]
    public void Info_Json_ReportsHeaderFields()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.CompressibleText(50_000);
        string input = tmp.WriteFile("doc.txt", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        var (code, output) = Run("info", result.Files[0], "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("doc.txt", doc.RootElement.GetProperty("fileName").GetString());
        Assert.True(doc.RootElement.GetProperty("compressed").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("encrypted").GetBoolean());
        Assert.Equal(content.Length, doc.RootElement.GetProperty("originalLength").GetInt64());
        Assert.Equal(16, doc.RootElement.GetProperty("eccParity").GetInt32());
    }

    [Fact]
    public void UnknownHeaderFlag_FailsWithUpdateMessage()
    {
        using var tmp = new TempDir();
        var layout = Layout.Create(900, 900, 3, 4, 16);
        byte[] payload = TestData.Random(500);
        var header = new ShardHeader
        {
            FileId = 0xDEADBEEF,
            Index = 0,
            Count = 1,
            PayloadLength = payload.Length,
            PayloadCrc32 = new Crc().Crc32(payload),
            TotalLength = payload.Length,
            OriginalLength = payload.Length,
            Flags = 0x40, // a flag bit this build does not define
            Sha256 = TestData.Sha256(payload),
            FileName = "future.bin",
        };
        byte[] headerBytes = header.Serialize();
        var stream = new byte[headerBytes.Length + payload.Length];
        headerBytes.CopyTo(stream, 0);
        payload.CopyTo(stream, headerBytes.Length);

        var renderer = new ShardRenderer();
        string path = tmp.File("future.png");
        renderer.RenderShard(layout, new Palette().Build(4), layout.PackMetadata(), stream, stream.Length,
            path, new RenderScratch(layout), renderer.CreateWriter("png", layout, AppSettings.Current));

        var ex = Assert.Throws<ShardDecodeException>(() => new ShardDecoder().DecodeImage(path));
        Assert.Contains("newer QrShard", ex.Message);
    }
}
