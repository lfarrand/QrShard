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

    private static int Workers(int budgetMB, int width, int height)
    {
        long perWorker = (long)width * height * ScratchBytesPerPixel;
        return (int)Math.Clamp(budgetMB * 1_000_000L / perWorker, 1, ShardDecoder.AutoParallelism);
    }

    [Fact]
    public void TheDefaultAffordsTheFullPoolAtMax4K()
    {
        // Max4K is the widest preset the encoder offers, and at the previous default of 4000 it was
        // the ONLY preset priced below the full pool — 20 workers where the smaller presets still
        // fitted 24. That asymmetry was invisible until the per-pixel estimate was corrected from 4
        // to 24 bytes, which is what made the ceiling bite.
        var settings = new AppSettings();

        Assert.Equal(ShardDecoder.AutoParallelism, Workers(settings.DecodeMemoryBudgetMB, 3840, 2160));
        Assert.Equal(ShardDecoder.AutoParallelism, Workers(settings.DecodeMemoryBudgetMB, 2160, 2160));
    }

    [Fact]
    public void TheDefaultStillThrottlesPhotoSizedInput()
    {
        // The budget has to keep meaning something. A 48-megapixel photo costs ~1.15 GB of scratch
        // per worker, so the pool must still be cut well below the full 24 — that throttling is the
        // whole point of the ceiling, and a default large enough to ignore it would be a default
        // that had quietly stopped working.
        int workers = Workers(new AppSettings().DecodeMemoryBudgetMB, 8000, 6000);

        Assert.True(workers < ShardDecoder.AutoParallelism,
            $"a 48 MP photo should still be throttled, got the full {workers} workers");
        Assert.True(workers >= 2, $"…but not down to a single worker, got {workers}");
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
