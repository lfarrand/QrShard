namespace QrShard;

/// <summary>
/// A 3x3 projective transform (homography) mapping one plane to another — the geometry of a
/// flat screen photographed from an angle. Solved from four point correspondences via the
/// standard direct linear transform (eight unknowns with h22 fixed at 1).
/// </summary>
internal sealed class Homography
{
    private readonly double[] _m; // row-major 3x3, m[8] == 1

    private Homography(double[] m) => _m = m;

    public (double X, double Y) Apply(double x, double y)
    {
        double w = _m[6] * x + _m[7] * y + _m[8];
        return ((_m[0] * x + _m[1] * y + _m[2]) / w,
                (_m[3] * x + _m[4] * y + _m[5]) / w);
    }

    /// <summary>Solves H such that H(from[i]) = to[i] for the four correspondences.</summary>
    public static Homography Solve(
        ReadOnlySpan<(double X, double Y)> from, ReadOnlySpan<(double X, double Y)> to)
    {
        if (from.Length != 4 || to.Length != 4)
            throw new ArgumentException("Exactly four correspondences are required.");

        // Rows: [x y 1 0 0 0 -x*u -y*u | u] and [0 0 0 x y 1 -x*v -y*v | v].
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            (double x, double y) = from[i];
            (double u, double v) = to[i];
            int r = 2 * i;
            a[r, 0] = x;
            a[r, 1] = y;
            a[r, 2] = 1;
            a[r, 6] = -x * u;
            a[r, 7] = -y * u;
            a[r, 8] = u;
            a[r + 1, 3] = x;
            a[r + 1, 4] = y;
            a[r + 1, 5] = 1;
            a[r + 1, 6] = -x * v;
            a[r + 1, 7] = -y * v;
            a[r + 1, 8] = v;
        }

        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 8; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                    pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-9)
                throw new ShardDecodeException("Degenerate finder geometry (collinear corners).");
            if (pivot != col)
                for (int k = 0; k <= 8; k++)
                    (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);

            for (int row = 0; row < 8; row++)
            {
                if (row == col)
                    continue;
                double factor = a[row, col] / a[col, col];
                if (factor == 0)
                    continue;
                for (int k = col; k <= 8; k++)
                    a[row, k] -= factor * a[col, k];
            }
        }

        var m = new double[9];
        for (int i = 0; i < 8; i++)
            m[i] = a[i, 8] / a[i, i];
        m[8] = 1;
        return new Homography(m);
    }
}
