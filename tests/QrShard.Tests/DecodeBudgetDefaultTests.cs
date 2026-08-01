namespace QrShard.Tests;

/// <summary>
/// The decode scratch budget is a declared ceiling, and its value decides the worker pool. Both
/// halves are worth pinning: the default must afford the full pool at the largest preset this tool
/// encodes, and it must stay inside the range the loader accepts.
/// </summary>
public class DecodeBudgetDefaultTests
{
    /// <summary>Bytes of concurrently-live scratch per source pixel; see ShardDecoder.</summary>
    private const int ScratchBytesPerPixel = 24;

    /// <summary>
    /// Workers the BUDGET alone affords, uncapped by core count. That distinction is the whole
    /// point: ShardDecoder takes min(AutoParallelism, affordable), so on a small machine the cores
    /// bind first and the budget never shows. A test that compared against AutoParallelism would
    /// therefore assert something about the RUNNER rather than about the default — which is
    /// exactly how the first version of this file passed on 32 threads and failed on CI's 4.
    /// </summary>
    private static long Affordable(int budgetMB, int width, int height) =>
        budgetMB * 1_000_000L / ((long)width * height * ScratchBytesPerPixel);

    /// <summary>The pool size the decoder would use on a machine with cores to spare.</summary>
    private const int FullPool = 24;

    [Fact]
    public void TheDefaultAffordsTheFullPoolAtMax4K()
    {
        // Max4K is the widest preset the encoder offers, and at the previous default of 4000 it was
        // the ONLY preset priced below the full pool — 20 workers where the smaller presets still
        // fitted 24. That asymmetry was invisible until the per-pixel estimate was corrected from 4
        // to 24 bytes, which is what made the ceiling bite.
        int budget = new AppSettings().DecodeMemoryBudgetMB;

        Assert.True(Affordable(budget, 3840, 2160) >= FullPool,
            $"Max4K affords only {Affordable(budget, 3840, 2160)} workers at {budget} MB");
        Assert.True(Affordable(budget, 2160, 2160) >= FullPool,
            $"2160² affords only {Affordable(budget, 2160, 2160)} workers at {budget} MB");
    }

    [Fact]
    public void TheDefaultStillThrottlesPhotoSizedInput()
    {
        // The budget has to keep meaning something. A 48-megapixel photo costs ~1.15 GB of scratch
        // per worker, so it must still price below the full pool — that throttling is the whole
        // point of the ceiling, and a default large enough to ignore it would be a default that had
        // quietly stopped working.
        int budget = new AppSettings().DecodeMemoryBudgetMB;
        long workers = Affordable(budget, 8000, 6000);

        Assert.True(workers < FullPool, $"a 48 MP photo should still be throttled, affords {workers}");
        Assert.True(workers >= 2, $"…but not down to a single worker, affords {workers}");
    }

    [Fact]
    public void TheDefaultIsInsideTheRangeTheLoaderAccepts()
    {
        // A default outside the validated range would make every run fail the moment anyone wrote
        // the value into their own appsettings.json.
        int fromJson = AppSettings.Load(WriteSettings(new AppSettings().DecodeMemoryBudgetMB)).DecodeMemoryBudgetMB;
        Assert.Equal(new AppSettings().DecodeMemoryBudgetMB, fromJson);

        static string WriteSettings(int budget)
        {
            string path = Path.Combine(Path.GetTempPath(), $"qrshard-budget-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, $"{{ \"DecodeMemoryBudgetMB\": {budget} }}");
            return path;
        }
    }
}
