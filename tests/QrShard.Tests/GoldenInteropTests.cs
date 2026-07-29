using System.Reflection;
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

    /// <summary>Minor lines covered by versions.json, deduplicated and ordered.</summary>
    private static List<(int Major, int Minor)> CoveredMinorLines =>
        RequiredVersions
            .Select(v => Version.Parse(v.TrimStart('v')))
            .Select(v => (v.Major, v.Minor))
            .Distinct()
            .OrderBy(v => v.Major).ThenBy(v => v.Minor)
            .ToList();

    /// <summary>The shipping version, from the assembly under test — the same
    /// AssemblyInformationalVersion the CLI's `--version` prints.</summary>
    private static Version CurrentVersion =>
        Version.Parse(typeof(QrShardCodec).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0]);

    [Fact]
    public void RequiredVersions_SpanEveryReleasedMinorLine()
    {
        // This previously asserted (1,0) (1,1) (1,2) (1,3) as literals, which meant the guard
        // needed hand-editing on exactly the occasion it was supposed to fire: v1.4.0 shipped and
        // nothing failed. Both halves are derived now, so neither needs maintaining.
        var lines = CoveredMinorLines;
        Assert.NotEmpty(lines);

        // 1. No gaps. A missing line between two present ones is an unpinned wire format, and it
        //    cannot be explained away as "not released yet" — something later than it is listed.
        for (int i = 1; i < lines.Count; i++)
        {
            var (prevMajor, prevMinor) = lines[i - 1];
            var (major, minor) = lines[i];
            bool contiguous = (major == prevMajor && minor == prevMinor + 1) ||
                              (major == prevMajor + 1 && minor == 0);
            Assert.True(contiguous,
                $"versions.json jumps from {prevMajor}.{prevMinor} to {major}.{minor}; the line(s) " +
                "between have no golden fixtures. Run golden/regenerate.ps1 for the missing tag(s).");
        }
    }

    [Fact]
    public void MinorLinesBelowCurrent_AllHaveFixtures()
    {
        // The drift this catches: a minor ships, nobody regenerates fixtures, and the wire format
        // it introduced is pinned by nothing. That went unnoticed through 1.3.10, 1.3.11 and
        // 1.3.12 because the only thing asking for it was a comment.
        //
        // It cannot simply demand coverage of the CURRENT version: a version-bump PR sets the
        // csproj to X.Y.0 before the tag exists, and regenerate.ps1 builds from a worktree of the
        // tag, so that PR structurally cannot carry its own fixtures. The floor below exempts
        // exactly that case and nothing else — once the version moves past X.Y.0, or on to the
        // next minor, the previous line must be covered.
        var current = CurrentVersion;
        (int Major, int Minor) floor =
            current.Build > 0 ? (current.Major, current.Minor)          // a patch: this line has shipped
            : current.Minor > 0 ? (current.Major, current.Minor - 1)    // X.Y.0: the line before it has
            : (current.Major - 1, 0);                                  // X.0.0: some earlier major has

        var highest = CoveredMinorLines[^1];
        bool covered = highest.Major > floor.Major ||
                       (highest.Major == floor.Major && highest.Minor >= floor.Minor);

        Assert.True(covered,
            $"the build is {current}, so golden fixtures should reach at least {floor.Major}.{floor.Minor}, " +
            $"but versions.json stops at {highest.Major}.{highest.Minor}. Tag the release, then run " +
            "golden/regenerate.ps1 and commit the fixtures.");
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
