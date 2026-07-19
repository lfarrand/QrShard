using QrShard;

namespace QrShard.Tests;

public class CrossShardFecTests
{
    private static byte[][] RandomShards(int count, int len, int seed)
    {
        var rng = new Random(seed);
        var shards = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            shards[i] = new byte[len];
            rng.NextBytes(shards[i]);
        }
        return shards;
    }

    private static byte[]?[] AllPresent(byte[][] data, byte[][] parity)
    {
        var present = new byte[]?[data.Length + parity.Length];
        for (int i = 0; i < data.Length; i++) present[i] = data[i];
        for (int i = 0; i < parity.Length; i++) present[data.Length + i] = parity[i];
        return present;
    }

    [Fact]
    public void Encode_ProducesRequestedParityCount()
    {
        var data = RandomShards(10, 100, 1);
        var parity = new CrossShardFec().Encode(data, 4, 100);
        Assert.Equal(4, parity.Length);
        Assert.All(parity, p => Assert.Equal(100, p.Length));
    }

    [Fact]
    public void AllShardsPresent_ReconstructsExactly()
    {
        var data = RandomShards(8, 200, 2);
        var parity = new CrossShardFec().Encode(data, 3, 200);
        Assert.True(new CrossShardFec().TryReconstruct(AllPresent(data, parity), 8, 3, 200, out var recovered));
        for (int i = 0; i < 8; i++)
            Assert.Equal(data[i], recovered[i]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LosingUpToParityDataShards_IsRecovered(int losses)
    {
        const int k = 12, p = 4, len = 128;
        var data = RandomShards(k, len, 10 + losses);
        var parity = new CrossShardFec().Encode(data, p, len);

        var present = AllPresent(data, parity);
        // Drop `losses` data shards at spread-out positions.
        foreach (int idx in Enumerable.Range(0, losses).Select(i => i * (k / losses)))
            present[idx] = null;

        Assert.True(new CrossShardFec().TryReconstruct(present, k, p, len, out var recovered));
        for (int i = 0; i < k; i++)
            Assert.Equal(data[i], recovered[i]);
    }

    [Fact]
    public void LosingMixOfDataAndParity_UpToParityTotal_IsRecovered()
    {
        const int k = 10, p = 4, len = 64;
        var data = RandomShards(k, len, 20);
        var parity = new CrossShardFec().Encode(data, p, len);
        var present = AllPresent(data, parity);

        // Lose 2 data + 2 parity = 4 total (== p). Still k survivors.
        present[1] = null;
        present[7] = null;
        present[k + 0] = null;
        present[k + 3] = null;

        Assert.True(new CrossShardFec().TryReconstruct(present, k, p, len, out var recovered));
        for (int i = 0; i < k; i++)
            Assert.Equal(data[i], recovered[i]);
    }

    [Fact]
    public void LosingMoreThanParity_Fails()
    {
        const int k = 10, p = 3, len = 64;
        var data = RandomShards(k, len, 30);
        var parity = new CrossShardFec().Encode(data, p, len);
        var present = AllPresent(data, parity);

        // Lose 4 > p=3 shards — fewer than k survivors.
        present[0] = present[1] = present[2] = present[3] = null;
        Assert.False(new CrossShardFec().TryReconstruct(present, k, p, len, out _));
    }

    [Fact]
    public void IsMds_EveryLossPatternOfSizeParity_Recovers()
    {
        // Exhaustively verify the MDS property: for a small code, EVERY subset of p lost shards
        // (across the full data+parity range) is recoverable.
        const int k = 6, p = 3, len = 32;
        var data = RandomShards(k, len, 40);
        var parity = new CrossShardFec().Encode(data, p, len);
        int n = k + p;

        int patterns = 0;
        foreach (var lost in Combinations(n, p))
        {
            var present = AllPresent(data, parity);
            foreach (int idx in lost)
                present[idx] = null;
            Assert.True(new CrossShardFec().TryReconstruct(present, k, p, len, out var recovered),
                $"loss pattern [{string.Join(",", lost)}] should recover");
            for (int i = 0; i < k; i++)
                Assert.Equal(data[i], recovered[i]);
            patterns++;
        }
        Assert.Equal(84, patterns); // C(9,3)
    }

    [Fact]
    public void ZeroParity_ReconstructsOnlyWhenAllDataPresent()
    {
        var data = RandomShards(5, 48, 50);
        var parity = new CrossShardFec().Encode(data, 0, 48);
        Assert.Empty(parity);

        Assert.True(new CrossShardFec().TryReconstruct(AllPresent(data, parity), 5, 0, 48, out var ok));
        for (int i = 0; i < 5; i++)
            Assert.Equal(data[i], ok[i]);

        var present = AllPresent(data, parity);
        present[2] = null;
        Assert.False(new CrossShardFec().TryReconstruct(present, 5, 0, 48, out _));
    }

    [Fact]
    public void SingleDataShard_WithParity_SurvivesDataLoss()
    {
        // Degenerate stripe: 1 data + 2 parity. Losing the data shard is still recoverable.
        var data = RandomShards(1, 100, 60);
        var parity = new CrossShardFec().Encode(data, 2, 100);
        var present = AllPresent(data, parity);
        present[0] = null; // lose the only data shard
        Assert.True(new CrossShardFec().TryReconstruct(present, 1, 2, 100, out var recovered));
        Assert.Equal(data[0], recovered[0]);
    }

    [Fact]
    public void MaxStripe_254DataPlus1Parity_Works()
    {
        const int k = 254, p = 1, len = 8;
        var data = RandomShards(k, len, 70);
        var parity = new CrossShardFec().Encode(data, p, len);
        var present = AllPresent(data, parity);
        present[100] = null;
        Assert.True(new CrossShardFec().TryReconstruct(present, k, p, len, out var recovered));
        Assert.Equal(data[100], recovered[100]);
    }

    [Fact]
    public void Encode_ExceedingStripeLimit_Throws() =>
        Assert.Throws<ArgumentException>(() => new CrossShardFec().Encode(RandomShards(200, 8, 1), 100, 8));

    [Fact]
    public void Encode_ZeroDataShards_Throws() =>
        Assert.Throws<ArgumentException>(() => new CrossShardFec().Encode([], 2, 8));

    [Fact]
    public void Reconstruct_FewerSurvivorsThanData_Fails()
    {
        // Only k-1 shards present overall → cannot solve regardless of which are missing.
        var data = RandomShards(5, 40, 80);
        var parity = new CrossShardFec().Encode(data, 2, 40);
        var present = AllPresent(data, parity);
        present[0] = present[1] = present[5] = null; // 4 survivors, need 5
        Assert.False(new CrossShardFec().TryReconstruct(present, 5, 2, 40, out _));
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (int j = i + 1; j < k; j++)
                idx[j] = idx[j - 1] + 1;
        }
    }
}
