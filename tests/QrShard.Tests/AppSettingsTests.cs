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
    public void ShippedSettingsFile_ParsesAndMatchesTheDocumentedDefault()
    {
        // The appsettings.json copied next to the executable (comments included) must parse,
        // and its shipped value must equal the built-in default.
        string shipped = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(shipped), "appsettings.json should be copied to output");
        Assert.Equal(CompressionLevel.Optimal, AppSettings.Load(shipped).PngCompressionLevel);
    }
}
