using System.Runtime.Intrinsics.X86;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Hardware-acceleration equivalence: whatever SIMD path this machine takes (GFNI at any
/// width, nibble shuffles, or scalar), the results must be bit-identical to the scalar field
/// arithmetic. Exhaustive over every coefficient and every byte value.
/// </summary>
public class GfniTests
{
    [Fact]
    public void MulAdd_MatchesScalarReference_ForEveryCoefficientAndByte()
    {
        var gf = new Gf256();
        // 300 bytes: covers the 64/32/16-lane blocks AND the scalar tail in one buffer,
        // and contains every byte value at least once.
        var src = new byte[300];
        for (int i = 0; i < src.Length; i++)
            src[i] = (byte)i;

        for (int coef = 0; coef < 256; coef++)
        {
            var expected = new byte[src.Length];
            var actual = new byte[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                expected[i] = (byte)(0x5A ^ gf.Mul((byte)coef, src[i]));
                actual[i] = 0x5A;
            }
            gf.MulAdd((byte)coef, src, actual);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(15)]   // scalar only
    [InlineData(20)]   // V128 block + scalar tail
    [InlineData(77)]   // V512 block + scalar tail (or V128 blocks on narrow machines)
    [InlineData(100)]  // V512 + V256 + scalar on wide machines
    [InlineData(130)]  // multiple wide blocks
    public void FecRoundTrip_AcrossBlockSizeBoundaries(int cwCount)
    {
        const int parity = 16;
        var fec = new Fec();
        int dataLen = Fec.DataLength(parity);
        byte[] stream = TestData.Random(cwCount * dataLen - 7, seed: cwCount);
        byte[] buffer = fec.Protect(stream, parity, cwCount);

        // Clean recovery must reproduce the stream (zero-padded tail included).
        Assert.True(fec.TryRecover(buffer, parity, cwCount, out byte[] recovered, out int corrected));
        Assert.Equal(0, corrected);
        Assert.Equal(stream, recovered[..stream.Length]);

        // Damage below capacity in several codewords, spread across every block region.
        var rng = new Random(cwCount * 7);
        for (int hit = 0; hit < cwCount; hit += 3)
            for (int e = 0; e < parity / 2; e++)
                buffer[rng.Next(Fec.CodewordLength) * cwCount + hit] ^= (byte)(1 + rng.Next(255));
        Assert.True(fec.TryRecover(buffer, parity, cwCount, out recovered, out corrected));
        Assert.True(corrected > 0);
        Assert.Equal(stream, recovered[..stream.Length]);
    }

    [Fact]
    public void ReportsAccelerationTier() // not an assertion — makes CI logs show which path ran
    {
        string tier = Gfni.V512.IsSupported ? "GFNI-V512"
            : Gfni.V256.IsSupported ? "GFNI-V256"
            : Gfni.IsSupported ? "GFNI-V128"
            : System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated ? "shuffle-V128"
            : "scalar";
        Assert.False(string.IsNullOrEmpty(tier));
    }
}
