using System.Security.Cryptography;
using System.Text.Json;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Cross-version wire-format interop: the CURRENT decoder must reconstruct shards produced by
/// EVERY released encoder, byte-for-byte. The fixtures under golden/ were encoded by the tagged
/// binaries themselves (see golden/regenerate.ps1) and are frozen — a change to the decoder
/// that breaks reading a shard someone encoded with an earlier release fails here, which no
/// encode-then-decode-with-the-current-build test can catch.
/// </summary>
public class GoldenInteropTests
{
    private sealed record Manifest(string Version, string Config, string ExpectedSha256, long ExpectedLength, string? Password);

    private static string GoldenRoot => Path.Combine(AppContext.BaseDirectory, "golden");

    public static IEnumerable<object[]> Fixtures()
    {
        if (!Directory.Exists(GoldenRoot))
            yield break;
        foreach (string manifestPath in Directory.EnumerateFiles(GoldenRoot, "manifest.json", SearchOption.AllDirectories))
            yield return [Path.GetRelativePath(GoldenRoot, Path.GetDirectoryName(manifestPath)!)];
    }

    [Fact]
    public void GoldenFixtures_ArePresent()
    {
        // Guards against the Content-copy silently dropping the fixtures (which would make the
        // theory below vacuously pass). Both released versions must contribute fixtures.
        var dirs = Fixtures().Select(f => (string)f[0]).ToList();
        Assert.Contains(dirs, d => d.StartsWith("v1.0.0"));
        Assert.Contains(dirs, d => d.StartsWith("v1.1.0"));
        Assert.True(dirs.Count >= 12, $"expected the full fixture matrix, found {dirs.Count}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void CurrentDecoder_ReconstructsEveryReleasedEncoding(string relativeDir)
    {
        string dir = Path.Combine(GoldenRoot, relativeDir);
        var manifest = JsonSerializer.Deserialize<Manifest>(
            File.ReadAllText(Path.Combine(dir, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var shards = Directory.GetFiles(dir, "*.png");
        Assert.NotEmpty(shards);

        using var tmp = new TempDir();
        string output = tmp.File("restored.out");
        // DecodeFolder verifies the payload CRCs and the whole-file SHA-256 internally; a
        // successful return is already proof of a bit-identical reconstruction, but we
        // re-check against the manifest independently as documentation and belt-and-braces.
        var restored = new ShardDecoder().DecodeFolder(shards, output, _ => { }, manifest.Password);
        Assert.Single(restored);

        byte[] decoded = File.ReadAllBytes(output);
        Assert.Equal(manifest.ExpectedLength, decoded.LongLength);
        string sha = Convert.ToHexStringLower(SHA256.HashData(decoded));
        Assert.Equal(manifest.ExpectedSha256, sha);
    }

    [Fact]
    public void EncryptedGolden_WrongPassword_StillFailsCleanly()
    {
        // Forward-compat of the failure path too: an old encrypted shard with the wrong password
        // must fail with the typed error, not decode to garbage.
        string dir = Path.Combine(GoldenRoot, "v1.0.0", "encrypted");
        if (!Directory.Exists(dir))
            return;
        var shards = Directory.GetFiles(dir, "*.png");
        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(shards, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => { }, "wrongpw"));
        Assert.Contains("wrong password", ex.Message);
    }
}
