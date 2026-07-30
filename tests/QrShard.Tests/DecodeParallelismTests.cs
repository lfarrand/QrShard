using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The automatic worker cap in <see cref="ShardDecoder.CollectShards"/> was raised from 16 to 24,
/// and nothing in the suite covered it in either direction — the cap could have been set to any
/// number, including one <see cref="AppSettings"/> rejects, and every test would still pass.
///
/// The invariant that makes changing it safe is that worker count is a throughput knob and
/// nothing more: <c>CollectShards</c> sorts the paths first and writes each result into its own
/// slot, so the shard sequence is a function of the file names, not of scheduling. Widening the
/// pool must therefore leave the decoded bytes and their order untouched. If someone ever
/// refactors that indexed write into a concurrent append, output becomes scheduler-dependent —
/// which would surface as intermittent, unreproducible corruption rather than a failing test.
/// </summary>
public class DecodeParallelismTests
{
    private static ShardDecoder DecoderWith(TempDir tmp, int parallelism, int budgetMB = 4000)
    {
        // AppSettings is a parsed file with no public mutators, so the setting under test has to
        // travel through a real settings file — the same route the CLI uses.
        string path = tmp.File($"appsettings-{parallelism}-{budgetMB}.json");
        File.WriteAllText(path,
            $$"""{ "DecodeMaxParallelism": {{parallelism}}, "DecodeMemoryBudgetMB": {{budgetMB}} }""");
        return new ShardDecoder(AppSettings.Load(path), new CameraRectifier(),
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());
    }

    [Fact]
    public void WideningTheWorkerPool_ChangesNeitherShardContentNorOrder()
    {
        using var tmp = new TempDir();
        // Enough images that the wide pool genuinely has more workers than the narrow one, and
        // that Parallel.For has several chunks to hand out.
        string input = tmp.WriteFile("input.bin", TestData.Random(400_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 2, BitsPerCell = 2 });
        Assert.True(result.Files.Count >= 8, $"expected several shards to spread across workers, got {result.Files.Count}");

        var serial = DecoderWith(tmp, 1).CollectShards(result.Files, _ => { });
        var wide = DecoderWith(tmp, 32).CollectShards(result.Files, _ => { });

        Assert.Equal(serial.Count, wide.Count);
        Assert.Equal(
            serial.Select(s => s.Header.Index),
            wide.Select(s => s.Header.Index));
        for (int i = 0; i < serial.Count; i++)
            Assert.Equal(serial[i].Payload, wide[i].Payload);
    }

    [Fact]
    public void DecodedFileIsIdentical_WhicheverWorkerCountProducedIt()
    {
        using var tmp = new TempDir();
        byte[] original = TestData.Random(400_000);
        string input = tmp.WriteFile("input.bin", original);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 2, BitsPerCell = 2 });

        DecoderWith(tmp, 1).DecodeFolder(result.Files, tmp.File("serial.bin"), _ => { });
        DecoderWith(tmp, 32).DecodeFolder(result.Files, tmp.File("wide.bin"), _ => { });

        Assert.Equal(original, File.ReadAllBytes(tmp.File("serial.bin")));
        Assert.Equal(original, File.ReadAllBytes(tmp.File("wide.bin")));
    }

    [Fact]
    public void MemoryBudget_TradesWorkersAwayWhenTheImagesAreLarge()
    {
        // ShardEncoder has always derived its degree from EncodeMemoryBudgetMB; the decode side
        // had no counterpart, so it took min(parallelism, images) and each worker then sized its
        // scratch from the largest image it saw. At the 500M-pixel per-image ceiling that is ~2 GB
        // per worker, times up to 24 — with the dimensions chosen by the sender, not the receiver.
        using var tmp = new TempDir();
        // Enough images that the ceiling (min of parallelism and image count) exceeds what the
        // budget affords — otherwise the budget cannot be the binding constraint and the test
        // would pass without exercising anything.
        string input = tmp.WriteFile("input.bin", TestData.Random(1_200_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 2, BitsPerCell = 2 });
        Assert.True(result.Files.Count >= 20, $"need enough images to exceed the budget; got {result.Files.Count}");

        // 900x900 is 810,000 px, so ~3.2 MB of scratch each: a 64 MB budget affords about 19
        // workers, fewer than the image count, so the budget binds rather than the ceiling.
        var log = new List<string>();
        var shards = DecoderWith(tmp, parallelism: 32, budgetMB: 64).CollectShards(result.Files, log.Add);

        Assert.Equal(result.Files.Count, shards.Count); // still decodes everything, just narrower
        Assert.Contains(log, l => l.Contains("decode worker(s) instead of"));
    }

    [Fact]
    public void MemoryBudget_LeavesOrdinaryCapturesAlone()
    {
        // The bound must not quietly cost throughput on real captures. At the default budget a
        // 900x900 image is ~3.2 MB of scratch, so nothing should be traded away and nothing should
        // be reported.
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(400_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 2, BitsPerCell = 2 });

        var log = new List<string>();
        var shards = DecoderWith(tmp, parallelism: 8, budgetMB: 4000).CollectShards(result.Files, log.Add);

        Assert.Equal(result.Files.Count, shards.Count);
        Assert.DoesNotContain(log, l => l.Contains("instead of"));
    }

    /// <summary>
    /// The automatic cap and the range <see cref="AppSettings"/> validates are declared in
    /// different files, so raising one past the other is an easy edit to make and an invisible
    /// one to catch: the automatic path assigns straight to the field without revalidating, so an
    /// out-of-range cap would be silently honoured while the identical value typed into
    /// appsettings.json is rejected.
    /// </summary>
    [Fact]
    public void AutoParallelism_IsAValueTheSettingsFileWouldAlsoAccept()
    {
        using var tmp = new TempDir();
        int auto = ShardDecoder.AutoParallelism;
        Assert.InRange(auto, 1, 1024);

        // Not a tautology against the constant: this asserts the value survives the parser.
        string path = tmp.File("appsettings.json");
        File.WriteAllText(path, $$"""{ "DecodeMaxParallelism": {{auto}} }""");
        Assert.Equal(auto, AppSettings.Load(path).DecodeMaxParallelism);
    }
}
