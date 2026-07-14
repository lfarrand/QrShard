using QrShard;

namespace QrShard.Tests;

public class FecTests
{
    private static byte[] RandomStream(int length, int seed = 9)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void ProtectRecover_RoundTripsCleanly()
    {
        const int parity = 16, cwCount = 5;
        byte[] stream = RandomStream(1000);
        byte[] buffer = Fec.Protect(stream, parity, cwCount);
        Assert.Equal(cwCount * Fec.CodewordLength, buffer.Length);

        Assert.True(Fec.TryRecover(buffer, parity, cwCount, out byte[] recovered, out int corrected));
        Assert.Equal(0, corrected);
        Assert.Equal(stream, recovered[..stream.Length]);
        Assert.All(recovered[stream.Length..], b => Assert.Equal(0, b)); // padding stays zero
    }

    [Fact]
    public void BurstDamage_IsSpreadAcrossCodewordsAndCorrected()
    {
        // A contiguous burst of B bytes lands on B/cwCount symbols per codeword thanks to
        // interleaving; parity 16 fixes 8 per codeword, so cwCount*8 contiguous bytes survive.
        const int parity = 16, cwCount = 5;
        byte[] stream = RandomStream(1000, seed: 10);
        byte[] buffer = Fec.Protect(stream, parity, cwCount);

        int burst = cwCount * 8; // exactly at capacity: 8 damaged symbols per codeword
        for (int i = 300; i < 300 + burst; i++)
            buffer[i] ^= 0xFF;

        Assert.True(Fec.TryRecover(buffer, parity, cwCount, out byte[] recovered, out int corrected));
        Assert.Equal(burst, corrected);
        Assert.Equal(stream, recovered[..stream.Length]);
    }

    [Fact]
    public void ExcessiveBurst_IsRejectedNotMiscorrected()
    {
        const int parity = 16, cwCount = 5;
        byte[] buffer = Fec.Protect(RandomStream(1000, seed: 11), parity, cwCount);

        int burst = cwCount * 12; // 12 damaged symbols per codeword — beyond the 8 correctable
        for (int i = 100; i < 100 + burst; i++)
            buffer[i] ^= 0xA7;

        Assert.False(Fec.TryRecover(buffer, parity, cwCount, out _, out _));
    }

    [Fact]
    public void ScatteredDamage_UpToPerCodewordLimit_IsCorrected()
    {
        const int parity = 32, cwCount = 8;
        byte[] stream = RandomStream(1500, seed: 12);
        byte[] buffer = Fec.Protect(stream, parity, cwCount);

        // Scatter damage with a hard cap of 14 bytes per codeword (limit is 16). Interleaving maps
        // buffer position p to codeword p % cwCount, so pick positions per residue class.
        var rng = new Random(13);
        int damaged = 0;
        for (int j = 0; j < cwCount; j++)
        {
            foreach (int i in Enumerable.Range(0, Fec.CodewordLength).OrderBy(_ => rng.Next()).Take(14))
            {
                buffer[i * cwCount + j] ^= (byte)rng.Next(1, 256);
                damaged++;
            }
        }

        Assert.True(Fec.TryRecover(buffer, parity, cwCount, out byte[] recovered, out int corrected));
        Assert.Equal(damaged, corrected);
        Assert.Equal(stream, recovered[..stream.Length]);
    }

    [Fact]
    public void StreamLargerThanCapacity_IsRejected() =>
        Assert.Throws<ArgumentException>(() => Fec.Protect(RandomStream(2000), 16, cwCount: 5));

    [Fact]
    public void CapacityMath_IsConsistent()
    {
        Assert.Equal(239, Fec.DataLength(16));
        Assert.Equal(255, Fec.DataLength(0));
        Assert.Equal(191, Fec.DataLength(64));
    }
}
