using QrShard;

namespace QrShard.Tests;

public class ReedSolomonTests
{
    private static byte[] MakeCodeword(int length, int parity, int seed)
    {
        var cw = new byte[length];
        new Random(seed).NextBytes(cw.AsSpan(0, length - parity));
        ReedSolomon.Encode(cw.AsSpan(0, length - parity), cw.AsSpan(length - parity));
        return cw;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void CleanCodeword_DecodesWithZeroCorrections(int parity)
    {
        byte[] cw = MakeCodeword(255, parity, seed: parity);
        byte[] original = [.. cw];
        Assert.True(ReedSolomon.TryDecode(cw, parity, out int corrected));
        Assert.Equal(0, corrected);
        Assert.Equal(original, cw);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void CorrectsExactlyMaxErrors(int parity)
    {
        // Inject parity/2 byte errors — the theoretical maximum — at random distinct positions
        // spanning both data and parity regions.
        byte[] cw = MakeCodeword(255, parity, seed: 100 + parity);
        byte[] original = [.. cw];
        var rng = new Random(200 + parity);
        var positions = Enumerable.Range(0, 255).OrderBy(_ => rng.Next()).Take(parity / 2).ToArray();
        foreach (int p in positions)
            cw[p] ^= (byte)rng.Next(1, 256);

        Assert.True(ReedSolomon.TryDecode(cw, parity, out int corrected));
        Assert.Equal(parity / 2, corrected);
        Assert.Equal(original, cw);
    }

    [Fact]
    public void ErrorsBeyondCapacity_AreDetectedNotMiscorrected()
    {
        const int parity = 16; // corrects 8
        byte[] cw = MakeCodeword(255, parity, seed: 7);
        var rng = new Random(8);
        foreach (int p in Enumerable.Range(0, 255).OrderBy(_ => rng.Next()).Take(12))
            cw[p] ^= (byte)rng.Next(1, 256);

        Assert.False(ReedSolomon.TryDecode(cw, parity, out _));
    }

    [Fact]
    public void ShortenedCodewords_Work()
    {
        // Codewords shorter than 255 (shortened RS) must encode and correct normally.
        const int parity = 16, length = 60;
        byte[] cw = MakeCodeword(length, parity, seed: 3);
        byte[] original = [.. cw];
        var rng = new Random(4);
        foreach (int p in Enumerable.Range(0, length).OrderBy(_ => rng.Next()).Take(8))
            cw[p] ^= (byte)rng.Next(1, 256);

        Assert.True(ReedSolomon.TryDecode(cw, parity, out int corrected));
        Assert.Equal(8, corrected);
        Assert.Equal(original, cw);
    }

    [Fact]
    public void ZeroParity_IsPassthrough()
    {
        var data = new byte[100];
        new Random(5).NextBytes(data);
        byte[] copy = [.. data];
        ReedSolomon.Encode(data, Span<byte>.Empty);
        Assert.True(ReedSolomon.TryDecode(data, 0, out int corrected));
        Assert.Equal(0, corrected);
        Assert.Equal(copy, data);
    }

    [Fact]
    public void AllZeroData_RoundTrips()
    {
        var cw = new byte[255];
        ReedSolomon.Encode(cw.AsSpan(0, 239), cw.AsSpan(239));
        Assert.All(cw, b => Assert.Equal(0, b)); // zero data → zero parity
        cw[17] = 0xAA;
        cw[200] = 0x55;
        Assert.True(ReedSolomon.TryDecode(cw, 16, out int corrected));
        Assert.Equal(2, corrected);
        Assert.All(cw, b => Assert.Equal(0, b));
    }

    [Fact]
    public void SingleErrorInEveryPosition_IsCorrected()
    {
        const int parity = 8;
        byte[] pristine = MakeCodeword(255, parity, seed: 11);
        for (int pos = 0; pos < 255; pos += 13)
        {
            byte[] cw = [.. pristine];
            cw[pos] ^= 0x42;
            Assert.True(ReedSolomon.TryDecode(cw, parity, out int corrected), $"position {pos}");
            Assert.Equal(1, corrected);
            Assert.Equal(pristine, cw);
        }
    }
}
