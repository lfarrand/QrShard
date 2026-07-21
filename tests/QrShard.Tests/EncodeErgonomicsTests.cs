using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Multi-input encode, --json output, APNG slideshow, and named profiles.</summary>
public class EncodeErgonomicsTests
{
    private static (int Code, string Out) Run(params string[] args)
    {
        var stdout = new StringWriter();
        int code = new Cli().Run(args, stdout, stdout);
        return (code, stdout.ToString());
    }

    [Fact]
    public void ApngSlideshow_HasAllFrames_AndEachDecodes()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });
        Assert.True(result.ImageCount >= 3);

        string apng = new SlideshowWriter().WriteApng(tmp.File("shards"), result.Files, 400);
        Assert.True(File.Exists(apng));

        using (var reloaded = Image.Load<Rgb24>(apng))
            Assert.Equal(result.ImageCount, reloaded.Frames.Count); // every shard is a full frame

        // The APNG decodes as a recording, byte-for-byte.
        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(apng, output, 8, _ => { }, out _);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void EncodeApngThenDecode_ThroughTheCli_RoundTrips()
    {
        // End-to-end through the CLI: `encode --slideshow apng` then `decode <file>.apng` — the
        // path that must recognize the .apng extension and route to the animated-image decoder.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000);
        string input = tmp.WriteFile("input.bin", content);
        string shards = tmp.File("shards");
        Assert.Equal(0, Run("encode", input, "-o", shards, "-r", "900", "--video", "--slideshow", "apng").Code);

        string apng = Path.Combine(shards, "slideshow.apng");
        Assert.True(File.Exists(apng));
        string output = tmp.File("out.bin");
        var (code, text) = Run("decode", apng, "-o", output);
        Assert.Equal(0, code);
        Assert.Contains("Restored", text);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void MultiFileEncode_BundlesAndExtracts()
    {
        using var tmp = new TempDir();
        byte[] a = TestData.Random(30_000, 1);
        byte[] b = TestData.Random(8_000, 2);
        string fa = tmp.WriteFile("a.bin", a);
        string fb = tmp.WriteFile("b.bin", b);
        string shards = tmp.File("shards");

        Assert.Equal(0, Run("encode", fa, fb, "-o", shards, "-r", "900").Code);
        string dest = tmp.File("restored");
        Assert.Equal(0, Run("decode", shards, "-o", dest).Code);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(dest, "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(dest, "b.bin")));
    }

    [Fact]
    public void EncodeJson_IsPureParseableJson()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(40_000));
        var (code, output) = Run("encode", input, "-o", tmp.File("shards"), "-r", "900", "--json");
        Assert.Equal(0, code);
        using var doc = System.Text.Json.JsonDocument.Parse(output); // throws if polluted
        Assert.True(doc.RootElement.GetProperty("imageCount").GetInt32() >= 1);
        Assert.NotEmpty(doc.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void Profile_AppliesPreset_AndFlagsStillOverride()
    {
        using var tmp = new TempDir();
        // appsettings with a "dense" profile.
        string settingsPath = tmp.File("appsettings.json");
        File.WriteAllText(settingsPath,
            """
            {
              "EncodeProfiles": {
                "dense": { "CellPx": 2, "BitsPerCell": 6, "EccParity": 8 }
              }
            }
            """);
        var settings = AppSettings.Load(settingsPath);
        Assert.True(settings.EncodeProfiles.ContainsKey("dense"));
        Assert.Equal(2, settings.EncodeProfiles["dense"].CellPx);
        Assert.Equal(6, settings.EncodeProfiles["dense"].BitsPerCell);
        Assert.Equal(3, settings.EncodeDefaults.CellPx); // defaults untouched

        // Via the CLI: profile sets cell 2, but an explicit -c 4 flag wins.
        string input = tmp.WriteFile("input.bin", TestData.Random(20_000));
        var stdout = new StringWriter();
        int code = new Cli(settings).Run(
            ["encode", input, "-o", tmp.File("shards"), "-r", "900", "--profile", "dense", "-c", "4"], stdout, stdout);
        Assert.Equal(0, code);
        Assert.Contains("cell 4px, 6 bits/cell", stdout.ToString()); // -c flag over profile, bits from profile
    }

    [Fact]
    public void UnknownProfile_IsRejected()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        var (code, output) = Run("encode", input, "-o", tmp.File("shards"), "-r", "900", "--profile", "nonesuch");
        Assert.Equal(2, code);
        Assert.Contains("unknown profile", output);
    }

    [Fact]
    public void MultiFile_SameBasenameDifferentDirs_RefusedNotSilentlyLost()
    {
        // Two DIFFERENT files with the same name from different folders would collide at the
        // archive root — an integrity tool must refuse, never silently drop one.
        using var tmp = new TempDir();
        string d1 = tmp.Sub("d1"), d2 = tmp.Sub("d2");
        File.WriteAllBytes(Path.Combine(d1, "same.bin"), TestData.Random(15_000, 1));
        File.WriteAllBytes(Path.Combine(d2, "same.bin"), TestData.Random(15_000, 2));

        var (code, output) = Run("encode", Path.Combine(d1, "same.bin"), Path.Combine(d2, "same.bin"),
            "-o", tmp.File("shards"), "-r", "900");
        Assert.Equal(1, code); // ArgumentException path → exit 1
        Assert.Contains("same archive path", output);
        Assert.False(Directory.Exists(tmp.File("shards"))); // nothing half-written
    }

    [Fact]
    public void MultiFile_SameNameInsideDifferentSubfolders_IsFine()
    {
        // The same name inside DIFFERENT subfolders keeps distinct paths — must round-trip.
        using var tmp = new TempDir();
        string proj = tmp.Sub("proj");
        Directory.CreateDirectory(Path.Combine(proj, "a"));
        Directory.CreateDirectory(Path.Combine(proj, "b"));
        byte[] ca = TestData.Random(9_000, 1), cb = TestData.Random(9_000, 2);
        File.WriteAllBytes(Path.Combine(proj, "a", "same.bin"), ca);
        File.WriteAllBytes(Path.Combine(proj, "b", "same.bin"), cb);

        string shards = tmp.File("shards");
        Assert.Equal(0, Run("encode", proj, "-o", shards, "-r", "900").Code);
        string dest = tmp.File("restored");
        Assert.Equal(0, Run("decode", shards, "-o", dest).Code);
        Assert.Equal(ca, File.ReadAllBytes(Path.Combine(dest, "a", "same.bin")));
        Assert.Equal(cb, File.ReadAllBytes(Path.Combine(dest, "b", "same.bin")));
    }
}
