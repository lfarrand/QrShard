using QrShard;

namespace QrShard.Tests;

public class Gf256Tests
{
    [Fact]
    public void Mul_ByZero_IsZero()
    {
        for (int a = 0; a < 256; a++)
        {
            Assert.Equal(0, Gf256.Mul((byte)a, 0));
            Assert.Equal(0, Gf256.Mul(0, (byte)a));
        }
    }

    [Fact]
    public void Mul_ByOne_IsIdentity()
    {
        for (int a = 0; a < 256; a++)
            Assert.Equal((byte)a, Gf256.Mul((byte)a, 1));
    }

    [Fact]
    public void Mul_IsCommutative()
    {
        for (int a = 0; a < 256; a += 7)
            for (int b = 0; b < 256; b += 5)
                Assert.Equal(Gf256.Mul((byte)a, (byte)b), Gf256.Mul((byte)b, (byte)a));
    }

    [Fact]
    public void Mul_IsAssociativeAndDistributive()
    {
        var rng = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            byte a = (byte)rng.Next(256), b = (byte)rng.Next(256), c = (byte)rng.Next(256);
            Assert.Equal(Gf256.Mul(Gf256.Mul(a, b), c), Gf256.Mul(a, Gf256.Mul(b, c)));
            byte lhs = Gf256.Mul(a, (byte)(b ^ c));
            byte rhs = (byte)(Gf256.Mul(a, b) ^ Gf256.Mul(a, c));
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
            Assert.Equal(a, Gf256.Div(Gf256.Mul(a, b), b));
        }
    }

    [Fact]
    public void Inv_MultipliesToOne()
    {
        for (int a = 1; a < 256; a++)
            Assert.Equal(1, Gf256.Mul((byte)a, Gf256.Inv((byte)a)));
    }

    [Fact]
    public void Div_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Gf256.Div(5, 0));

    [Fact]
    public void Inv_OfZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Gf256.Inv(0));

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

        Gf256.MulAdd(coef, src, dst);
        for (int i = 0; i < src.Length; i++)
            Assert.Equal((byte)(seed[i] ^ Gf256.Mul(coef, src[i])), dst[i]);
    }

    [Fact]
    public void MulAdd_WithZeroCoef_IsNoOp()
    {
        var src = new byte[16];
        new Random(4).NextBytes(src);
        var dst = new byte[16];
        var copy = (byte[])dst.Clone();
        Gf256.MulAdd(0, src, dst);
        Assert.Equal(copy, dst);
    }

    [Fact]
    public void Invert_IdentityMatrix_IsIdentity()
    {
        int n = 5;
        var m = Identity(n);
        Assert.True(Gf256.Invert(m, n));
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
                original[i][j] = Gf256.Inv((byte)((i + 1) ^ (n + 1 + j)));
        }
        var inverse = original.Select(r => (byte[])r.Clone()).ToArray();
        Assert.True(Gf256.Invert(inverse, n));

        // original * inverse == identity
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                byte acc = 0;
                for (int k = 0; k < n; k++)
                    acc ^= Gf256.Mul(original[i][k], inverse[k][j]);
                Assert.Equal(i == j ? (byte)1 : (byte)0, acc);
            }
    }

    [Fact]
    public void Invert_SingularMatrix_ReturnsFalse()
    {
        int n = 3;
        var m = Identity(n);
        m[2] = (byte[])m[1].Clone(); // duplicate row → singular
        Assert.False(Gf256.Invert(m, n));
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
