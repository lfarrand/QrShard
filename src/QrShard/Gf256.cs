namespace QrShard;

/// <summary>
/// Arithmetic over GF(2^8) with the same primitive polynomial 0x11D used by <see cref="ReedSolomon"/>,
/// plus dense-matrix inversion. This backs the cross-shard erasure code (<see cref="CrossShardFec"/>),
/// which reconstructs whole missing images from parity images and therefore needs to solve linear
/// systems over the field — something the syndrome-based <see cref="ReedSolomon"/> path does not do.
/// </summary>
internal static class Gf256
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

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
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Div(byte a, byte b)
    {
        if (b == 0)
            throw new DivideByZeroException("GF(256) division by zero.");
        return a == 0 ? (byte)0 : Exp[Log[a] - Log[b] + 255];
    }

    public static byte Inv(byte a)
    {
        if (a == 0)
            throw new DivideByZeroException("GF(256) has no inverse of zero.");
        return Exp[255 - Log[a]];
    }

    /// <summary>
    /// Multiply-accumulate a whole shard: dst[i] ^= coef * src[i] over GF(2^8).
    /// This is the inner loop of both encoding and reconstruction.
    /// </summary>
    public static void MulAdd(byte coef, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (coef == 0)
            return;
        int logC = Log[coef];
        for (int i = 0; i < src.Length; i++)
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
    public static bool Invert(byte[][] matrix, int n)
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
