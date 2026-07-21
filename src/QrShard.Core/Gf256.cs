using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace QrShard;

/// <summary>
/// Arithmetic over GF(2^8) with the same primitive polynomial 0x11D used by <see cref="ReedSolomon"/>,
/// plus dense-matrix inversion. This backs the cross-shard erasure code (<see cref="CrossShardFec"/>),
/// which reconstructs whole missing images from parity images and therefore needs to solve linear
/// systems over the field — something the syndrome-based <see cref="ReedSolomon"/> path does not do.
/// </summary>
internal sealed class Gf256
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];
    private static readonly ulong[] AffineMatrices = new ulong[256];

    static Gf256()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
                x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++)
            Exp[i] = Exp[i - 255];

        for (int c = 0; c < 256; c++)
            AffineMatrices[c] = BuildAffineMatrix((byte)c);
    }

    private static byte MulStatic(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    /// <summary>
    /// The 8x8 GF(2) bit matrix computing y = coef·x in our field (0x11D), packed for
    /// GF2P8AFFINEQB: multiplication by a constant is linear over GF(2), and GFNI's affine
    /// instruction applies an arbitrary bit matrix per byte — one instruction replaces the
    /// two-shuffle nibble trick regardless of the field polynomial. Output bit i's row lives
    /// in qword byte 7-i; row bit k weights input bit k.
    /// </summary>
    private static ulong BuildAffineMatrix(byte coef)
    {
        Span<byte> rows = stackalloc byte[8];
        for (int k = 0; k < 8; k++)
        {
            byte column = MulStatic(coef, (byte)(1 << k)); // where input bit k lands in the output
            for (int i = 0; i < 8; i++)
                if (((column >> i) & 1) != 0)
                    rows[i] |= (byte)(1 << k);
        }
        ulong matrix = 0;
        for (int i = 0; i < 8; i++)
            matrix |= (ulong)rows[i] << (8 * (7 - i));
        return matrix;
    }

    /// <summary>Packed GFNI affine matrix for multiply-by-<paramref name="coef"/>.</summary>
    public ulong AffineMatrix(byte coef) => AffineMatrices[coef];

    public byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public byte Div(byte a, byte b)
    {
        if (b == 0)
            throw new DivideByZeroException("GF(256) division by zero.");
        return a == 0 ? (byte)0 : Exp[Log[a] - Log[b] + 255];
    }

    public byte Inv(byte a)
    {
        if (a == 0)
            throw new DivideByZeroException("GF(256) has no inverse of zero.");
        return Exp[255 - Log[a]];
    }

    /// <summary>α^i for the field's generator α.</summary>
    public byte AlphaPower(int i) => Exp[i % 255];

    /// <summary>
    /// Builds the two 16-entry nibble product tables for multiplication by a fixed coefficient:
    /// coef*b = coef*(b_hi·16) ^ coef*b_lo, so a pair of byte shuffles multiplies 16 lanes at once.
    /// </summary>
    public (Vector128<byte> Lo, Vector128<byte> Hi) MulTables(byte coef)
    {
        Span<byte> lo = stackalloc byte[16];
        Span<byte> hi = stackalloc byte[16];
        for (int n = 0; n < 16; n++)
        {
            lo[n] = Mul(coef, (byte)n);
            hi[n] = Mul(coef, (byte)(n << 4));
        }
        return (Vector128.Create<byte>(lo), Vector128.Create<byte>(hi));
    }

    /// <summary>Multiplies all 16 lanes by the fixed coefficient encoded in the nibble tables.</summary>
    public Vector128<byte> MulVec(Vector128<byte> v, Vector128<byte> tableLo, Vector128<byte> tableHi)
    {
        var nibble = Vector128.Create((byte)0x0F);
        return Vector128.ShuffleNative(tableLo, v & nibble)
             ^ Vector128.ShuffleNative(tableHi, Vector128.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & nibble);
    }

    /// <summary>
    /// Multiply-accumulate a whole shard: dst[i] ^= coef * src[i] over GF(2^8).
    /// This is the inner loop of both encoding and reconstruction.
    ///
    /// Vectorized via the classic nibble-shuffle technique: a GF product by a fixed coefficient
    /// splits as coef*b = coef*(b_hi·16) ^ coef*b_lo, so two 16-entry product tables used as
    /// byte-shuffle sources multiply 16 bytes per step.
    /// </summary>
    public void MulAdd(byte coef, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (coef == 0)
            return;

        int i = 0;
        if (Gfni.IsSupported)
        {
            // GFNI: one GF2P8AFFINEQB per vector replaces the shuffle dance, at up to 64
            // bytes per instruction with AVX-512.
            ulong matrix = AffineMatrices[coef];
            if (Gfni.V512.IsSupported && src.Length - i >= 64)
            {
                var m512 = Vector512.Create(matrix).AsByte();
                for (; i <= src.Length - 64; i += 64)
                {
                    var product = Gfni.V512.GaloisFieldAffineTransform(Vector512.Create<byte>(src.Slice(i, 64)), m512, 0);
                    (Vector512.Create<byte>(dst.Slice(i, 64)) ^ product).CopyTo(dst.Slice(i, 64));
                }
            }
            if (Gfni.V256.IsSupported && src.Length - i >= 32)
            {
                var m256 = Vector256.Create(matrix).AsByte();
                for (; i <= src.Length - 32; i += 32)
                {
                    var product = Gfni.V256.GaloisFieldAffineTransform(Vector256.Create<byte>(src.Slice(i, 32)), m256, 0);
                    (Vector256.Create<byte>(dst.Slice(i, 32)) ^ product).CopyTo(dst.Slice(i, 32));
                }
            }
            if (src.Length - i >= 16)
            {
                var m128 = Vector128.Create(matrix).AsByte();
                for (; i <= src.Length - 16; i += 16)
                {
                    var product = Gfni.GaloisFieldAffineTransform(Vector128.Create<byte>(src.Slice(i, 16)), m128, 0);
                    (Vector128.Create<byte>(dst.Slice(i, 16)) ^ product).CopyTo(dst.Slice(i, 16));
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated && src.Length >= 16)
        {
            var (tableLo, tableHi) = MulTables(coef);
            for (; i <= src.Length - 16; i += 16)
            {
                var v = Vector128.Create<byte>(src.Slice(i, 16));
                var product = MulVec(v, tableLo, tableHi);
                (Vector128.Create<byte>(dst.Slice(i, 16)) ^ product).CopyTo(dst.Slice(i, 16));
            }
        }

        int logC = Log[coef];
        for (; i < src.Length; i++)
        {
            byte s = src[i];
            if (s != 0)
                dst[i] ^= Exp[logC + Log[s]];
        }
    }

    /// <summary>
    /// Inverts an n x n matrix in place via Gauss-Jordan elimination over GF(2^8).
    /// Returns false if the matrix is singular (should never happen for a Cauchy submatrix).
    /// </summary>
    public bool Invert(byte[][] matrix, int n)
    {
        // Augment with identity.
        var aug = new byte[n][];
        for (int r = 0; r < n; r++)
        {
            aug[r] = new byte[2 * n];
            Array.Copy(matrix[r], aug[r], n);
            aug[r][n + r] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            while (pivot < n && aug[pivot][col] == 0)
                pivot++;
            if (pivot == n)
                return false; // singular
            if (pivot != col)
                (aug[col], aug[pivot]) = (aug[pivot], aug[col]);

            byte inv = Inv(aug[col][col]);
            for (int j = 0; j < 2 * n; j++)
                aug[col][j] = Mul(aug[col][j], inv);

            for (int r = 0; r < n; r++)
            {
                if (r == col)
                    continue;
                byte factor = aug[r][col];
                if (factor == 0)
                    continue;
                for (int j = 0; j < 2 * n; j++)
                    aug[r][j] ^= Mul(factor, aug[col][j]);
            }
        }

        for (int r = 0; r < n; r++)
            Array.Copy(aug[r], n, matrix[r], 0, n);
        return true;
    }
}
