using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>The v2 permuted interleave: metadata version 3, deterministic scatter/gather, and
/// the vertical-damage spreading the classic modular interleave cannot guarantee.</summary>
public class Interleave2Tests
{
    private static readonly EncodeOptions FastV2 = new()
    {
        Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4, Interleave2 = true,
    };

    [Fact]
    public void Permutation_IsDeterministicBijection()
    {
        var a = new Interleaver2().Permutation(40_000);
        var b = new Interleaver2().Permutation(40_000);
        Assert.Equal(a, b); // same on both sides of a transfer, by construction
        Assert.Equal(40_000, a.Distinct().Count());
        Assert.Equal(0, a.Min());
        Assert.Equal(39_999, a.Max());
    }

    [Fact]
    public void ScatterGather_RoundTripsBytesAndFlags()
    {
        const int n = 10_000;
        var interleaver = new Interleaver2();
        byte[] classic = TestData.Random(n, 11);
        var scattered = new byte[n];
        var back = new byte[n];
        interleaver.Scatter(classic, scattered, n);
        interleaver.Gather(scattered, back, n);
        Assert.Equal(classic, back);
        Assert.NotEqual(classic, scattered); // it actually permuted
    }

    [Fact]
    public async Task LargePermutation_IsSingleFlightAndOnlyWeaklyRetained()
    {
        // Just above the 32 MiB strong-cache ceiling. Before the single-flight gate, simultaneous
        // encode workers each built and retained their own ~34 MiB int array.
        const int n = 8_400_000;
        var interleaver = new Interleaver2();
        using var start = new ManualResetEventSlim(false);
        Task<int[]>[] callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return interleaver.Permutation(n);
        })).ToArray();

        start.Set();
        int[][] results = await Task.WhenAll(callers);

        Assert.All(results, permutation => Assert.Same(results[0], permutation));
        Assert.Equal(1, interleaver.PermutationBuilds);
        Assert.Equal(0, interleaver.CachedBytes); // large result is a weak hand-off, not a process-lifetime pin
        Assert.Equal(n, results[0].Length);
    }

    [Fact]
    public void GatherStreams_ReusesOnePermutationForAllConfidenceInputs()
    {
        const int n = 10_000;
        var interleaver = new Interleaver2();
        byte[] classic = TestData.Random(n, 41);
        var scattered = new byte[n];
        interleaver.Scatter(classic, scattered, n);
        int[] permutation = interleaver.Permutation(n);
        var scatteredFlags = new bool[n];
        for (int i = 0; i < n; i++)
            scatteredFlags[permutation[i]] = (i & 1) == 0;
        byte[] scatteredSecond = scattered.Select(b => (byte)(b ^ 0x5a)).ToArray();
        var cells = new byte[n];
        var flags = new bool[n];
        var second = new byte[n];

        interleaver.GatherStreams(scattered, cells, scatteredFlags, flags,
            scatteredSecond, second, n);

        Assert.Equal(classic, cells);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal((i & 1) == 0, flags[i]);
            Assert.Equal((byte)(classic[i] ^ 0x5a), second[i]);
        }
    }

    [Fact]
    public void Permutation_DestroysArithmeticConcentration()
    {
        // The failure mode v2 exists for: bytes damaged at a FIXED STRIDE (a vertical blob
        // through row-major cells). Under the classic map their codewords can concentrate;
        // under the permutation the worst-hit codeword must stay near the uniform mean.
        const int cwCount = 155;
        int n = cwCount * Fec.CodewordLength;
        int[] perm = new Interleaver2().Permutation(n);
        var inverse = new int[n];
        for (int i = 0; i < n; i++)
            inverse[perm[i]] = i;

        foreach (int stride in new[] { cwCount, cwCount * 2, 310, 620 })
        {
            var hits = new int[cwCount];
            int count = 0;
            for (int pos = 0; pos < n && count < 12 * cwCount; pos += stride, count++)
                hits[inverse[pos] % cwCount]++;
            double mean = (double)count / cwCount;
            Assert.True(hits.Max() <= mean * 3 + 4,
                $"stride {stride}: worst codeword took {hits.Max()} hits vs mean {mean:0.0}");
        }
    }

    [Fact]
    public void V2_RoundTripsCleanAndCarriesMetadataVersion3()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(60_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), FastV2);

        var diag = new ShardDecoder().Diagnose(result.Files[0]);
        Assert.NotNull(diag.Layout);
        Assert.True(diag.Layout!.Interleave2);

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void V2_SurvivesVerticalStripDamage_AtClassicHostileStride()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), FastV2);

        var probe = new ShardDecoder().Diagnose(result.Files[0]);
        var layout = probe.Layout!;

        // A full-height vertical gray strip: damage at a fixed stride through the cell stream —
        // the exact pattern the classic map can concentrate. The permutation is deterministic
        // (seeded by the layout), so compute the EXACT per-codeword hit counts and pick the
        // widest strip that (a) defeats errors-only decoding somewhere (> parity/2 hits) while
        // (b) staying within erasure capacity everywhere (<= parity hits).
        int cwCount = layout.CodewordCount;
        int protectedLength = cwCount * Fec.CodewordLength;
        int[] perm = new Interleaver2().Permutation(protectedLength);
        var inverse = new int[protectedLength];
        for (int i = 0; i < protectedLength; i++)
            inverse[perm[i]] = i;
        int gx0 = layout.GridW / 2;
        int bits = layout.BitsPerCell;

        int stripCols = 0;
        for (int candidate = 24; candidate >= 2; candidate -= 2)
        {
            var counts = new int[cwCount];
            var touched = new HashSet<int>();
            for (int gy = 0; gy < layout.GridH; gy++)
                for (int gx = gx0; gx < gx0 + candidate; gx++)
                {
                    long firstBit = (long)(gy * layout.GridW + gx) * bits;
                    for (long b = firstBit >> 3; b <= (firstBit + bits - 1) >> 3; b++)
                        if (b < protectedLength && touched.Add((int)b))
                            counts[inverse[b] % cwCount]++;
                }
            if (counts.Max() <= layout.EccParity && counts.Max() > layout.EccParity / 2)
            {
                stripCols = candidate;
                break;
            }
        }
        Assert.True(stripCols > 0, "no strip width defeats errors-only while staying within erasure capacity");

        int dataX = Layout.Border + layout.DataLeft;
        int dataY = layout.ContentTop + Layout.Border + layout.DataTop;

        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            for (int y = dataY; y < dataY + layout.GridH * layout.CellPx; y++)
                for (int x = dataX + gx0 * layout.CellPx; x < dataX + (gx0 + stripCols) * layout.CellPx; x++)
                    img[x, y] = new Rgb24(128, 128, 128);
            img.SaveAsPng(result.Files[0]);
        }

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([result.Files[0]], output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void V2_RequiresEcc()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        Assert.Throws<ArgumentException>(() => new ShardEncoder().Encode(
            input, tmp.Sub("shards"), FastV2 with { EccParity = 0 }));
    }

    [Fact]
    public void V2_WorksWithParityRecoveryAndFountain()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), FastV2 with { RecoveryPercent = 25 });
        var survivors = result.Files.Where((_, i) => i != 0).ToList();

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(survivors, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
