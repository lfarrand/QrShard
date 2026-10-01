using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Exact nearest-colour lookup for a measured palette that is not a channel product.
/// A 16-wide grid answers a sample that sits near a palette colour from the cells around it.
/// The result matches <see cref="Palette.Nearest"/>: the lowest index wins a distance tie.
/// Rebuilt per palette; the buckets are reused across rows of one capture.
/// </summary>
internal sealed class MeasuredColorIndex
{
    private const int Side = 16;
    private const int Cells = 16;
    private readonly Dictionary<int, List<int>> _cells = new();
    private readonly List<List<int>> _pool = new();
    private Rgb24[] _colors = [];

    public void Rebuild(Rgb24[] palette)
    {
        foreach (List<int> list in _cells.Values)
        {
            list.Clear();
            _pool.Add(list);
        }
        _cells.Clear();
        _colors = palette;
        for (int i = 0; i < palette.Length; i++)
        {
            int key = Key(palette[i].R, palette[i].G, palette[i].B);
            if (!_cells.TryGetValue(key, out List<int>? bucket))
                _cells[key] = bucket = Rent();
            bucket.Add(i);
        }
    }

    public int Nearest(int r, int g, int b, out long distance) =>
        Search(r, g, b, exclude: -1, out distance);

    /// <summary>Nearest entry other than <paramref name="exclude"/>, lowest index on a tie.</summary>
    public int SecondNearest(int r, int g, int b, int exclude, out long distance) =>
        Search(r, g, b, exclude, out distance);

    private int Search(int r, int g, int b, int exclude, out long distance)
    {
        long bestDist = long.MaxValue;
        int best = exclude >= 0 ? exclude : 0;
        int cr = r >> 4, cg = g >> 4, cb = b >> 4;
        for (int rad = 0; rad <= Cells; rad++)
        {
            if (rad > 0 && bestDist < long.MaxValue && RingMinSquared(r, g, b, rad) > bestDist)
                break;
            VisitRing(cr, cg, cb, rad, r, g, b, exclude, ref bestDist, ref best);
            // An exact hit lives in this sample's own cell, including a duplicate colour.
            if (bestDist == 0)
                break;
            // Nothing nearby, or the nearby hit is too far to prune the rest of the cube.
            // The linear scan is the same answer and cheaper than walking every ring.
            if (rad == 2 && (bestDist == long.MaxValue || bestDist > 48L * 48))
            {
                ScanAll(r, g, b, exclude, ref bestDist, ref best);
                break;
            }
        }
        distance = bestDist;
        return best;
    }

    private void VisitRing(int cr, int cg, int cb, int rad, int r, int g, int b, int exclude,
        ref long bestDist, ref int best)
    {
        if (rad == 0)
        {
            Consider(cr, cg, cb, r, g, b, exclude, ref bestDist, ref best);
            return;
        }
        for (int dr = -rad; dr <= rad; dr++)
        {
            for (int dg = -rad; dg <= rad; dg++)
            {
                for (int db = -rad; db <= rad; db++)
                {
                    if (Math.Max(Math.Abs(dr), Math.Max(Math.Abs(dg), Math.Abs(db))) != rad)
                        continue;
                    Consider(cr + dr, cg + dg, cb + db, r, g, b, exclude, ref bestDist, ref best);
                }
            }
        }
    }

    private void Consider(int cr, int cg, int cb, int r, int g, int b, int exclude,
        ref long bestDist, ref int best)
    {
        if ((uint)cr >= Cells || (uint)cg >= Cells || (uint)cb >= Cells)
            return;
        if (!_cells.TryGetValue((cr << 8) | (cg << 4) | cb, out List<int>? bucket))
            return;
        foreach (int i in bucket)
        {
            if (i == exclude)
                continue;
            long d = Distance(r, g, b, _colors[i]);
            if (d < bestDist || (d == bestDist && i < best))
            {
                bestDist = d;
                best = i;
            }
        }
    }

    private void ScanAll(int r, int g, int b, int exclude, ref long bestDist, ref int best)
    {
        bestDist = long.MaxValue;
        best = exclude >= 0 ? exclude : 0;
        for (int i = 0; i < _colors.Length; i++)
        {
            if (i == exclude)
                continue;
            long d = Distance(r, g, b, _colors[i]);
            if (d < bestDist || (d == bestDist && i < best))
            {
                bestDist = d;
                best = i;
            }
        }
    }

    private List<int> Rent()
    {
        int n = _pool.Count;
        if (n == 0)
            return new List<int>();
        List<int> list = _pool[n - 1];
        _pool.RemoveAt(n - 1);
        return list;
    }

    private static int Key(int r, int g, int b) => ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);

    private static long RingMinSquared(int r, int g, int b, int rad)
    {
        long d = AxisRing(r, rad);
        d = Math.Min(d, AxisRing(g, rad));
        d = Math.Min(d, AxisRing(b, rad));
        return d == long.MaxValue ? long.MaxValue : d * d;
    }

    /// <summary>Pixels from <paramref name="v"/> to the nearest cell <paramref name="rad"/> steps away.</summary>
    private static long AxisRing(int v, int rad)
    {
        int cell = v >> 4;
        int local = v - (cell << 4);
        long best = long.MaxValue;
        if (cell + rad < Cells)
            best = (long)Side * rad - local;
        if (cell - rad >= 0)
            best = Math.Min(best, (long)Side * rad + local - (Side - 1));
        return best;
    }

    private static long Distance(int r, int g, int b, Rgb24 colour)
    {
        long dr = r - colour.R, dg = g - colour.G, db = b - colour.B;
        return dr * dr + dg * dg + db * db;
    }
}
