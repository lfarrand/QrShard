using System.Runtime.Intrinsics;
using QrShard;

namespace QrShard.Tests;

public class Gf256Tests
{
    [Fact]
    public void Mul_ByZero_IsZero()
    {
        for (int a = 0; a < 256; a++)
        {
            Assert.Equal(0, new Gf256().Mul((byte)a, 0));
            Assert.Equal(0, new Gf256().Mul(0, (byte)a));
        }
    }

    [Fact]
    public void Mul_ByOne_IsIdentity()
    {
        for (int a = 0; a < 256; a++)
            Assert.Equal((byte)a, new Gf256().Mul((byte)a, 1));
    }

    [Fact]
    public void Mul_IsCommutative()
    {
        for (int a = 0; a < 256; a += 7)
            for (int b = 0; b < 256; b += 5)
                Assert.Equal(new Gf256().Mul((byte)a, (byte)b), new Gf256().Mul((byte)b, (byte)a));
    }

    [Fact]
    public void Mul_IsAssociativeAndDistributive()
    {
        var rng = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            byte a = (byte)rng.Next(256), b = (byte)rng.Next(256), c = (byte)rng.Next(256);
            Assert.Equal(new Gf256().Mul(new Gf256().Mul(a, b), c), new Gf256().Mul(a, new Gf256().Mul(b, c)));
            byte lhs = new Gf256().Mul(a, (byte)(b ^ c));
            byte rhs = (byte)(new Gf256().Mul(a, b) ^ new Gf256().Mul(a, c));
            Assert.Equal(lhs, rhs);
        }
    }

    [Fact]
    public void DivIsInverseOfMul()
    {
        var rng = new Random(2);
        for (int i = 0; i < 3000; i++)
        {
            byte a = (byte)rng.Next(256), b = (byte)rng.Next(1, 256);
            Assert.Equal(a, new Gf256().Div(new Gf256().Mul(a, b), b));
        }
    }

    [Fact]
    public void Inv_MultipliesToOne()
    {
        for (int a = 1; a < 256; a++)
            Assert.Equal(1, new Gf256().Mul((byte)a, new Gf256().Inv((byte)a)));
    }

    [Fact]
    public void Div_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => new Gf256().Div(5, 0));

    [Fact]
    public void Inv_OfZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => new Gf256().Inv(0));

    [Fact]
    public void MulAdd_AccumulatesLinearCombination()
    {
        var rng = new Random(3);
        var src = new byte[64];
        rng.NextBytes(src);
        byte coef = 0xB7;
        var dst = new byte[64];
        var seed = new byte[64];
        rng.NextBytes(seed);
        seed.CopyTo(dst, 0);

        new Gf256().MulAdd(coef, src, dst);
        for (int i = 0; i < src.Length; i++)
            Assert.Equal((byte)(seed[i] ^ new Gf256().Mul(coef, src[i])), dst[i]);
    }

    [Fact]
    public void MulAdd_WithZeroCoef_IsNoOp()
    {
        var src = new byte[16];
        new Random(4).NextBytes(src);
        var dst = new byte[16];
        var copy = (byte[])dst.Clone();
        new Gf256().MulAdd(0, src, dst);
        Assert.Equal(copy, dst);
    }

    [Fact]
    public void AlphaPower_MatchesRepeatedMultiplication()
    {
        byte alpha = new Gf256().AlphaPower(1);
        byte acc = 1;
        for (int i = 0; i < 300; i++) // crosses the 255-cycle wrap
        {
            Assert.Equal(acc, new Gf256().AlphaPower(i));
            acc = new Gf256().Mul(acc, alpha);
        }
    }

    [Fact]
    public void MulVec_MatchesScalarMul_ForAllLanes()
    {
        if (!Vector128.IsHardwareAccelerated)
            return;
        var rng = new Random(6);
        Span<byte> input = stackalloc byte[16];
        foreach (byte coef in new byte[] { 1, 2, 0x1D, 0x80, 0xFF, 29 })
        {
            var (lo, hi) = new Gf256().MulTables(coef);
            rng.NextBytes(input);
            var v = Vector128.Create<byte>(input);
            var product = new Gf256().MulVec(v, lo, hi);
            for (int lane = 0; lane < 16; lane++)
                Assert.Equal(new Gf256().Mul(coef, input[lane]), product.GetElement(lane));
        }
    }

    [Fact]
    public void Invert_IdentityMatrix_IsIdentity()
    {
        int n = 5;
        var m = Identity(n);
        Assert.True(new Gf256().Invert(m, n));
        Assert.Equal(Identity(n), m);
    }

    [Fact]
    public void Invert_TimesOriginal_IsIdentity()
    {
        var rng = new Random(5);
        int n = 6;
        // Build an invertible matrix from a Cauchy construction (always non-singular).
        var original = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            original[i] = new byte[n];
            for (int j = 0; j < n; j++)
                original[i][j] = new Gf256().Inv((byte)((i + 1) ^ (n + 1 + j)));
        }
        var inverse = original.Select(r => (byte[])r.Clone()).ToArray();
        Assert.True(new Gf256().Invert(inverse, n));

        // original * inverse == identity
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                byte acc = 0;
                for (int k = 0; k < n; k++)
                    acc ^= new Gf256().Mul(original[i][k], inverse[k][j]);
                Assert.Equal(i == j ? (byte)1 : (byte)0, acc);
            }
    }

    [Fact]
    public void Invert_SingularMatrix_ReturnsFalse()
    {
        int n = 3;
        var m = Identity(n);
        m[2] = (byte[])m[1].Clone(); // duplicate row → singular
        Assert.False(new Gf256().Invert(m, n));
    }

    private static byte[][] Identity(int n)
    {
        var m = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            m[i] = new byte[n];
            m[i][i] = 1;
        }
        return m;
    }
}
