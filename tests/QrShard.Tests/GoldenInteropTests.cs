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

    // Every version's fixture set must at least cover this core matrix. New configs (e.g.
    // interleave2, present only from v1.1.0) are allowed on top; these must always be there.
    private static readonly string[] CoreConfigs =
        ["compressed", "raw", "parity", "fountain", "encrypted", "highecc", "camera"];

    [Fact]
    public void GoldenFixtures_ArePresent_AndEachVersionIsComplete()
    {
        // Guards against the Content-copy silently dropping fixtures (which would make the theory
        // below vacuously pass). Rather than a hardcoded floor, this scales with the matrix: it
        // discovers every version directory actually present and asserts each carries the full
        // core config set — so a partially-dropped copy, or a newly added version missing configs,
        // fails here.
        Assert.True(Directory.Exists(GoldenRoot), "golden fixtures were not copied to the test output");
        var byVersion = Fixtures()
            .Select(f => (string)f[0])
            .Select(rel => (Version: rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0],
                            Config: Path.GetFileName(rel)))
            .GroupBy(x => x.Version)
            .ToList();

        Assert.True(byVersion.Count >= 2, $"expected fixtures from multiple released versions, found {byVersion.Count}");
        foreach (var version in byVersion)
        {
            var configs = version.Select(x => x.Config).ToHashSet();
            var missing = CoreConfigs.Where(c => !configs.Contains(c)).ToList();
            Assert.True(missing.Count == 0, $"golden version '{version.Key}' is missing config(s): {string.Join(", ", missing)}");
        }
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
