using System.Formats.Tar;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>
/// The whole promise, end to end, for the archive payload: several real files go into a tar, the
/// tar becomes shard images, the images are screenshotted, the screenshots are decoded, the tar
/// comes back, and the files come out of it byte-for-byte.
///
/// Existing coverage tested pieces of this — round trips of single files, archive extraction from
/// a directly-constructed tar, screenshot re-encoding — but nothing walked the whole chain, and in
/// particular nothing asserted that the REASSEMBLED ARCHIVE is byte-identical before extraction.
/// That distinction matters: extraction compares file contents, so a tar whose headers or padding
/// were subtly wrong could still extract correctly and hide a codec defect.
/// </summary>
public class ArchiveRoundTripTests
{
    /// <summary>Big enough that the archive spans several images, so reassembly ordering and the
    /// multi-shard path are exercised rather than a single-image happy path.</summary>
    private static readonly EncodeOptions Screen = new()
    {
        Width = 900, Height = 900, CellPx = 4, BitsPerCell = 3,
    };

    /// <summary>
    /// A source tree with deliberately varied members: incompressible random bytes, highly
    /// compressible text, an empty file, a single-byte file, and a nested folder — so the tar
    /// carries directory entries and non-trivial paths rather than one flat list.
    /// </summary>
    private static Dictionary<string, byte[]> BuildSourceTree(string root, int seed)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["random-a.bin"] = TestData.Random(15_000, seed),
            ["random-b.bin"] = TestData.Random(9_000, seed + 1),
            ["text.txt"] = TestData.CompressibleText(6_000),
            ["empty.bin"] = [],
            ["one-byte.bin"] = [0xC5],
            ["nested/deep/random-c.bin"] = TestData.Random(12_000, seed + 2),
            ["nested/notes.txt"] = TestData.CompressibleText(1_200),
        };
        foreach (var (rel, bytes) in files)
        {
            string path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        return files;
    }

    private static string BuildTar(string sourceRoot, string tarPath)
    {
        TarFile.CreateFromDirectory(sourceRoot, tarPath, includeBaseDirectory: false);
        return tarPath;
    }

    /// <summary>
    /// What a screenshot actually is: the shard drawn somewhere on a larger desktop, then written
    /// out by an OS tool. The offset is deliberately not centred and not even, because a capture
    /// that only ever lands on a tidy boundary tests less than it appears to.
    /// </summary>
    private static string Screenshot(string shardPath, string destPath, int index)
    {
        using var shard = Image.Load<Rgb24>(shardPath);
        int w = shard.Width + 137 + index * 3, h = shard.Height + 91 + index * 5;
        using var desktop = new Image<Rgb24>(w, h, new Rgb24(58, 60, 64));
        desktop.Mutate(c => c.DrawImage(shard, new Point(53 + index, 39 + index * 2), 1f));
        desktop.SaveAsPng(destPath);
        return destPath;
    }

    private static List<string> Screenshots(IReadOnlyList<string> shards, string dir)
    {
        var shots = new List<string>();
        for (int i = 0; i < shards.Count; i++)
            shots.Add(Screenshot(shards[i], Path.Combine(dir, $"screenshot-{i:D3}.png"), i));
        return shots;
    }

    private static void AssertTreesMatch(Dictionary<string, byte[]> expected, string actualRoot)
    {
        var actual = Directory.GetFiles(actualRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(actualRoot, p).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

        // Compare the SETS first: a missing or extra file is a different failure from a corrupt
        // one, and saying which it is up front makes the diagnosis immediate.
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (rel, bytes) in expected)
            Assert.True(bytes.AsSpan().SequenceEqual(actual[rel]),
                $"'{rel}' differs: expected {bytes.Length} bytes, got {actual[rel].Length}");
    }

    [Fact]
    public void AnArchiveSurvivesTheFullScreenshotRoundTripAndExtractsByteForByte()
    {
        using var tmp = new TempDir();
        var expected = BuildSourceTree(tmp.Sub("source"), seed: 4242);
        string tar = BuildTar(tmp.Sub("source"), tmp.File("bundle.tar"));

        // IsArchive is what makes the decoder extract rather than write the tar out as a file.
        var encoded = new ShardEncoder().Encode(tar, tmp.Sub("shards"), Screen with { IsArchive = true });
        Assert.True(encoded.Files.Count > 1, $"expected a multi-image archive, got {encoded.Files.Count}");

        var shots = Screenshots(encoded.Files, tmp.Sub("shots"));

        string extractTo = tmp.Sub("restored");
        var restored = new ShardDecoder().DecodeFolder(shots, extractTo, _ => { });

        var only = Assert.Single(restored);
        Assert.Equal(extractTo, only.OutputPath);
        AssertTreesMatch(expected, extractTo);
    }

    [Fact]
    public void TheReassembledArchiveIsByteIdenticalBeforeItIsExtracted()
    {
        // Same chain, but the tar is carried as an ORDINARY payload so the decoder hands it back
        // as a file instead of extracting it. That is the only way to compare the archive itself,
        // and it is worth comparing: extraction checks file CONTENTS, so a tar with subtly wrong
        // headers or padding could still extract correctly and hide the defect.
        using var tmp = new TempDir();
        var expected = BuildSourceTree(tmp.Sub("source"), seed: 99);
        string tar = BuildTar(tmp.Sub("source"), tmp.File("bundle.tar"));
        byte[] originalArchive = File.ReadAllBytes(tar);

        var encoded = new ShardEncoder().Encode(tar, tmp.Sub("shards"), Screen);
        Assert.True(encoded.Files.Count > 1, $"expected a multi-image archive, got {encoded.Files.Count}");

        var shots = Screenshots(encoded.Files, tmp.Sub("shots"));

        string recoveredTar = tmp.File("recovered.tar");
        new ShardDecoder().DecodeFolder(shots, recoveredTar, _ => { });

        // 1. The archive itself, byte for byte.
        byte[] recovered = File.ReadAllBytes(recoveredTar);
        Assert.Equal(originalArchive.Length, recovered.Length);
        Assert.True(originalArchive.AsSpan().SequenceEqual(recovered), "the reassembled tar differs from the original");
        Assert.Equal(TestData.Sha256(originalArchive), TestData.Sha256(recovered));

        // 2. And it is a real archive, not merely identical bytes: extract it and check the files.
        string extractTo = tmp.Sub("extracted");
        TarFile.ExtractToDirectory(recoveredTar, extractTo, overwriteFiles: false);
        AssertTreesMatch(expected, extractTo);
    }

    [Fact]
    public void ADestroyedScreenshotIsReportedRatherThanSilentlyLosingFiles()
    {
        // The negative control, and the reason to trust the three tests above. If the decoder were
        // somehow not consuming these screenshots at all — reading a cached payload, or a stale
        // temp file — every positive assertion here would still pass. Destroying one image has to
        // break the round trip, and it has to break it by SAYING SO rather than by quietly
        // extracting a partial tree.
        using var tmp = new TempDir();
        BuildSourceTree(tmp.Sub("source"), seed: 314);
        string tar = BuildTar(tmp.Sub("source"), tmp.File("bundle.tar"));

        var encoded = new ShardEncoder().Encode(tar, tmp.Sub("shards"), Screen with { IsArchive = true });
        var shots = Screenshots(encoded.Files, tmp.Sub("shots"));

        // Paint the whole of one screenshot flat: no frame, no strips, nothing recoverable.
        using (var wrecked = new Image<Rgb24>(600, 600, new Rgb24(58, 60, 64)))
            wrecked.SaveAsPng(shots[^1]);

        string extractTo = tmp.Sub("restored");
        var thrown = Record.Exception(() => new ShardDecoder().DecodeFolder(shots, extractTo, _ => { }));

        Assert.IsType<ShardDecodeException>(thrown);
        Assert.False(Directory.Exists(extractTo) && Directory.GetFiles(extractTo, "*", SearchOption.AllDirectories).Length > 0,
            "a failed archive decode must not leave a partial extraction behind");
    }

    [Fact]
    public void TheArchiveStillRestoresWhenTheScreenshotsArriveOutOfOrder()
    {
        // Shards are ordered by the index in their header, not by filename, but a folder decode
        // sorts by path first — so a screenshot set whose names do not match capture order is the
        // ordinary case, not an exotic one, and the archive must not depend on it.
        using var tmp = new TempDir();
        var expected = BuildSourceTree(tmp.Sub("source"), seed: 7);
        string tar = BuildTar(tmp.Sub("source"), tmp.File("bundle.tar"));

        var encoded = new ShardEncoder().Encode(tar, tmp.Sub("shards"), Screen with { IsArchive = true });
        Assert.True(encoded.Files.Count > 1, $"expected a multi-image archive, got {encoded.Files.Count}");

        // Names that sort into the exact reverse of capture order.
        string dir = tmp.Sub("shuffled");
        var shots = new List<string>();
        for (int i = 0; i < encoded.Files.Count; i++)
            shots.Add(Screenshot(encoded.Files[i], Path.Combine(dir, $"shot-{encoded.Files.Count - i:D3}.png"), i));

        string extractTo = tmp.Sub("restored");
        new ShardDecoder().DecodeFolder(shots, extractTo, _ => { });
        AssertTreesMatch(expected, extractTo);
    }
}
