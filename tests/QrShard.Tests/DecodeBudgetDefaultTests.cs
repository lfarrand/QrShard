namespace QrShard.Tests;

/// <summary>
/// The decode planning budget decides the worker pool. Pin both its conservative 4K/photo
/// behaviour and its settings-file contract.
/// </summary>
public class DecodeBudgetDefaultTests
{
    /// <summary>Bytes of concurrently-live scratch per source pixel; see ShardDecoder.</summary>
    private const int ScratchBytesPerPixel = 40;

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
    public void TheDefaultKeepsUsefulButBoundedParallelismAtMax4K()
    {
        // Repeated round-robin measurements found little difference between 12 and 24 workers,
        // while the wider pool doubles the concurrency risk on adversarial camera inputs.
        int budget = new AppSettings().DecodeMemoryBudgetMB;
        long workers = Affordable(budget, 3840, 2160);

        Assert.InRange(workers, 8, FullPool - 1);
    }

    [Fact]
    public void TheDefaultStillThrottlesPhotoSizedInput()
    {
        // A 48-megapixel camera input is planned at ~1.92 GB per worker. Keep two-way progress
        // without recreating the six-worker, ~11 GB private-memory peak reproduced in review.
        int budget = new AppSettings().DecodeMemoryBudgetMB;
        long workers = Affordable(budget, 8000, 6000);

        Assert.Equal(2, workers);
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
