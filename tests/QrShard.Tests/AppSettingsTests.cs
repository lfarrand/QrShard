using System.IO.Compression;
using QrShard;

namespace QrShard.Tests;

public class AppSettingsTests
{
    [Fact]
    public void MissingFile_UsesDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json"));
        Assert.Equal(CompressionLevel.Optimal, settings.PngCompressionLevel);
    }

    [Fact]
    public void FileWithCommentsAndTrailingComma_Parses()
    {
        using var tmp = new TempDir();
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path,
            """
            {
              // comments are allowed, like standard .NET appsettings files
              "PngCompressionLevel": "Fastest", // trailing commas too
            }
            """);
        Assert.Equal(CompressionLevel.Fastest, AppSettings.Load(path).PngCompressionLevel);
    }

    [Theory]
    [InlineData("optimal", CompressionLevel.Optimal)]
    [InlineData("FASTEST", CompressionLevel.Fastest)]
    [InlineData("SmallestSize", CompressionLevel.SmallestSize)]
    [InlineData("NoCompression", CompressionLevel.NoCompression)]
    public void Level_IsCaseInsensitive(string value, CompressionLevel expected)
    {
        using var tmp = new TempDir();
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, $$"""{ "PngCompressionLevel": "{{value}}" }""");
        Assert.Equal(expected, AppSettings.Load(path).PngCompressionLevel);
    }

    [Fact]
    public void MissingSetting_UsesDefault()
    {
        using var tmp = new TempDir();
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, """{ "SomethingElse": 1 }""");
        Assert.Equal(CompressionLevel.Optimal, AppSettings.Load(path).PngCompressionLevel);
    }

    [Fact]
    public void InvalidLevel_FailsLoudlyWithTheValidValues()
    {
        using var tmp = new TempDir();
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, """{ "PngCompressionLevel": "Turbo" }""");
        var ex = Assert.Throws<InvalidOperationException>(() => AppSettings.Load(path));
        Assert.Contains("Turbo", ex.Message);
        Assert.Contains("Optimal, Fastest, SmallestSize, NoCompression", ex.Message);
    }

    [Fact]
    public void MalformedJson_FailsLoudly()
    {
        using var tmp = new TempDir();
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, "{ not json ");
        var ex = Assert.Throws<InvalidOperationException>(() => AppSettings.Load(path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void ShippedSettingsFile_ParsesAndMatchesTheBuiltInDefaults()
    {
        // The appsettings.json copied next to the executable (comments included) must parse,
        // and every shipped value must equal the corresponding built-in default.
        string shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(shipped), "appsettings.json should be copied to output");
        var fromFile = AppSettings.Load(shipped);
        var builtIn = AppSettings.Load("no-such-file.json");

        Assert.Equal(builtIn.PngCompressionLevel, fromFile.PngCompressionLevel);
        Assert.Equal(builtIn.PayloadCompressionLevel, fromFile.PayloadCompressionLevel);
        Assert.Equal(builtIn.ShardFolderSuffix, fromFile.ShardFolderSuffix);
        Assert.Equal(builtIn.EncodeMemoryBudgetMB, fromFile.EncodeMemoryBudgetMB);
        Assert.Equal(builtIn.DecodeMaxParallelism, fromFile.DecodeMaxParallelism);
        Assert.Equal(builtIn.EncodeDefaults.Resolution, fromFile.EncodeDefaults.Resolution);
        Assert.Equal(builtIn.EncodeDefaults.CellPx, fromFile.EncodeDefaults.CellPx);
        Assert.Equal(builtIn.EncodeDefaults.BitsPerCell, fromFile.EncodeDefaults.BitsPerCell);
        Assert.Equal(builtIn.EncodeDefaults.EccParity, fromFile.EncodeDefaults.EccParity);
        Assert.Equal(builtIn.EncodeDefaults.RecoveryPercent, fromFile.EncodeDefaults.RecoveryPercent);
        Assert.Equal(builtIn.EncodeDefaults.ImageFormat, fromFile.EncodeDefaults.ImageFormat);
        Assert.Equal(builtIn.EncodeDefaults.Compress, fromFile.EncodeDefaults.Compress);
    }

    // ---------- Encode defaults + tuning settings ----------

    private static AppSettings LoadJson(TempDir tmp, string json)
    {
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, json);
        return AppSettings.Load(path);
    }

    [Fact]
    public void EncodeDefaults_FullObject_Parses()
    {
        using var tmp = new TempDir();
        var settings = LoadJson(tmp,
            """
            {
              "EncodeDefaults": {
                "Resolution": "3840x2160", "CellPx": 1, "BitsPerCell": 6,
                "EccParity": 32, "RecoveryPercent": 10, "ImageFormat": "QOI", "Compress": false
              },
              "ShardFolderSuffix": "-shards",
              "PayloadCompressionLevel": "SmallestSize",
              "EncodeMemoryBudgetMB": 512,
              "DecodeMaxParallelism": 4
            }
            """);
        Assert.Equal("3840x2160", settings.EncodeDefaults.Resolution);
        Assert.Equal(1, settings.EncodeDefaults.CellPx);
        Assert.Equal(6, settings.EncodeDefaults.BitsPerCell);
        Assert.Equal(32, settings.EncodeDefaults.EccParity);
        Assert.Equal(10, settings.EncodeDefaults.RecoveryPercent);
        Assert.Equal("qoi", settings.EncodeDefaults.ImageFormat); // normalized
        Assert.False(settings.EncodeDefaults.Compress);
        Assert.Equal("-shards", settings.ShardFolderSuffix);
        Assert.Equal(CompressionLevel.SmallestSize, settings.PayloadCompressionLevel);
        Assert.Equal(512, settings.EncodeMemoryBudgetMB);
        Assert.Equal(4, settings.DecodeMaxParallelism);
    }

    [Fact]
    public void EncodeDefaults_PartialObject_KeepsOtherDefaults()
    {
        using var tmp = new TempDir();
        var settings = LoadJson(tmp, """{ "EncodeDefaults": { "CellPx": 2 } }""");
        Assert.Equal(2, settings.EncodeDefaults.CellPx);
        Assert.Equal("2160", settings.EncodeDefaults.Resolution);
        Assert.Equal(4, settings.EncodeDefaults.BitsPerCell);
        Assert.True(settings.EncodeDefaults.Compress);
    }

    [Theory]
    [InlineData("""{ "EncodeDefaults": { "CellPx": 0 } }""", "CellPx")]
    [InlineData("""{ "EncodeDefaults": { "BitsPerCell": 9 } }""", "BitsPerCell")]
    [InlineData("""{ "EncodeDefaults": { "EccParity": 15 } }""", "EccParity")]
    [InlineData("""{ "EncodeDefaults": { "RecoveryPercent": 150 } }""", "RecoveryPercent")]
    [InlineData("""{ "EncodeDefaults": { "Resolution": "banana" } }""", "Resolution")]
    [InlineData("""{ "EncodeDefaults": { "ImageFormat": "gif" } }""", "ImageFormat")]
    [InlineData("""{ "ShardFolderSuffix": "" }""", "ShardFolderSuffix")]
    [InlineData("""{ "EncodeMemoryBudgetMB": 10 } """, "EncodeMemoryBudgetMB")]
    [InlineData("""{ "DecodeMaxParallelism": -1 }""", "DecodeMaxParallelism")]
    [InlineData("""{ "PayloadCompressionLevel": "Turbo" }""", "PayloadCompressionLevel")]
    public void InvalidValues_FailLoudly_NamingTheSetting(string json, string expectedInMessage)
    {
        using var tmp = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(() => LoadJson(tmp, json));
        Assert.Contains(expectedInMessage, ex.Message);
    }
}
