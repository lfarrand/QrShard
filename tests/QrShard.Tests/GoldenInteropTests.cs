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

    private sealed record VersionManifest(string[] Versions);

    /// <summary>The versions that must have fixtures, shared with regenerate.ps1 so the set on
    /// disk and the set demanded here cannot drift apart.</summary>
    private static string[] RequiredVersions =>
        JsonSerializer.Deserialize<VersionManifest>(
            File.ReadAllText(Path.Combine(GoldenRoot, "versions.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Versions;

    [Fact]
    public void EveryRequiredVersion_HasACompleteFixtureSet()
    {
        // The previous form of this asserted only `count >= 2`, which the two fixture directories
        // that happened to exist satisfied permanently — so the guard against wire-format drift
        // sat green while ten further versions shipped with no fixtures at all. The required set
        // now comes from versions.json, so a release that should be covered and is not fails here
        // instead of passing silently.
        Assert.True(Directory.Exists(GoldenRoot), "golden fixtures were not copied to the test output");

        var present = Fixtures()
            .Select(f => (string)f[0])
            .Select(rel => (Version: rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0],
                            Config: Path.GetFileName(rel)))
            .GroupBy(x => x.Version)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Config).ToHashSet());

        var absent = RequiredVersions.Where(v => !present.ContainsKey(v)).ToList();
        Assert.True(absent.Count == 0,
            $"versions.json requires golden fixtures for {string.Join(", ", absent)}, but none are present. " +
            "Run tests/QrShard.Tests/golden/regenerate.ps1 and commit the result.");

        foreach (string version in RequiredVersions)
        {
            var missing = CoreConfigs.Where(c => !present[version].Contains(c)).ToList();
            Assert.True(missing.Count == 0,
                $"golden version '{version}' is missing config(s): {string.Join(", ", missing)}");
        }

        // A fixture directory nobody requires is either a leftover or an unrecorded addition;
        // either way versions.json is the thing that should have been updated.
        var unlisted = present.Keys.Except(RequiredVersions).ToList();
        Assert.True(unlisted.Count == 0,
            $"golden fixtures exist for {string.Join(", ", unlisted)} but versions.json does not list them.");
    }

    [Fact]
    public void RequiredVersions_SpanEveryReleasedMinorLine()
    {
        // The policy versions.json encodes: one entry per minor line, because the wire format is
        // versioned by a metadata nibble that patch releases do not touch. This asserts the list
        // actually satisfies that rather than trusting the comment above it.
        var minors = RequiredVersions
            .Select(v => Version.Parse(v.TrimStart('v')))
            .Select(v => (v.Major, v.Minor))
            .ToHashSet();

        Assert.Contains((1, 0), minors);
        Assert.Contains((1, 1), minors);
        Assert.Contains((1, 2), minors);
        Assert.Contains((1, 3), minors);
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
