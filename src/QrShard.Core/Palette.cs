using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Builds the color palette for a given bits-per-cell density. Colors are spread across the RGB
/// cube with maximal per-channel spacing so that nearest-color classification is robust.
/// </summary>
internal sealed class Palette
{
    public const int MinBits = 1;
    public const int MaxBits = 10;

    public Rgb24[] Build(int bitsPerCell)
    {
        if (bitsPerCell is < MinBits or > MaxBits)
            throw new ArgumentOutOfRangeException(nameof(bitsPerCell));

        if (bitsPerCell == 1)
            return [new Rgb24(0, 0, 0), new Rgb24(255, 255, 255)];

        var (nR, nG, nB) = ChannelCounts(bitsPerCell);

        var colors = new Rgb24[1 << bitsPerCell];
        for (int i = 0; i < colors.Length; i++)
        {
            int iR = i / (nG * nB);
            int iG = i / nB % nG;
            int iB = i % nB;
            colors[i] = new Rgb24(Level(iR, nR), Level(iG, nG), Level(iB, nB));
        }
        return colors;
    }

    private static byte Level(int index, int count) =>
        count == 1 ? (byte)0 : (byte)(index * 255 / (count - 1));

    /// <summary>
    /// How many levels each channel carries: bits are distributed R first, then G, then B. The
    /// palette index is the mixed-radix digit string (iR, iG, iB) in that order — the layout
    /// <see cref="SeparablePalette"/> relies on, so both must read it from here.
    /// </summary>
    public static (int R, int G, int B) ChannelCounts(int bitsPerCell) =>
        (1 << ((bitsPerCell + 2) / 3), 1 << ((bitsPerCell + 1) / 3), 1 << (bitsPerCell / 3));

    /// <summary>
    /// The nearest palette color EXCLUDING one index, with its squared distance — the
    /// classification margin (erasure flagging) and the alternative value (Chase trials).
    /// </summary>
    public int SecondNearest(Rgb24[] palette, int r, int g, int b, int excludeIndex, out long distance)
    {
        int best = excludeIndex;
        distance = long.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            if (i == excludeIndex)
                continue;
            long dr = r - palette[i].R, dg = g - palette[i].G, db = b - palette[i].B;
            long dist = dr * dr + dg * dg + db * db;
            if (dist < distance)
            {
                distance = dist;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Index of the palette color nearest (squared RGB distance) to the sample.</summary>
    public int Nearest(Rgb24[] palette, int r, int g, int b)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int dr = r - palette[i].R, dg = g - palette[i].G, db = b - palette[i].B;
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Squared RGB distance between two colours.</summary>
    internal static long DistanceSquared(Rgb24 a, Rgb24 b)
    {
        long dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>
    /// Minimum pairwise squared distance. Zero when any two entries share a colour. The grid is
    /// sized from an upper bound, so every pair that could be the minimum falls in a neighbouring
    /// cell and the result matches a full scan.
    /// </summary>
    internal static long ClosestSquared(Rgb24[] colors)
    {
        int n = colors.Length;
        if (n < 2)
            return long.MaxValue;

        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int cmp = colors[a].R.CompareTo(colors[b].R);
            if (cmp != 0)
                return cmp;
            cmp = colors[a].G.CompareTo(colors[b].G);
            return cmp != 0 ? cmp : colors[a].B.CompareTo(colors[b].B);
        });

        long upper = long.MaxValue;
        for (int i = 1; i < n; i++)
        {
            int limit = Math.Min(n - 1, i + 7);
            for (int j = i; j <= limit; j++)
            {
                long d = DistanceSquared(colors[order[i - 1]], colors[order[j]]);
                if (d == 0)
                    return 0;
                if (d < upper)
                    upper = d;
            }
        }

        int side = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(upper)));
        var grid = new Dictionary<long, List<int>>();
        long best = upper;
        for (int i = 0; i < n; i++)
        {
            Rgb24 p = colors[i];
            int cr = p.R / side, cg = p.G / side, cb = p.B / side;
            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dg = -1; dg <= 1; dg++)
                {
                    for (int db = -1; db <= 1; db++)
                    {
                        if (!grid.TryGetValue(CellKey(cr + dr, cg + dg, cb + db), out List<int>? bucket))
                            continue;
                        foreach (int j in bucket)
                        {
                            long d = DistanceSquared(p, colors[j]);
                            if (d < best)
                                best = d;
                        }
                    }
                }
            }

            long own = CellKey(cr, cg, cb);
            if (!grid.TryGetValue(own, out List<int>? mine))
                grid[own] = mine = new List<int>();
            mine.Add(i);
        }
        return best;
    }

    /// <summary>Squared length of the axis-aligned bounding-box diagonal — an upper bound on the widest pair.</summary>
    internal static long AabbDiagonalSquared(Rgb24[] colors)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        foreach (Rgb24 c in colors)
        {
            if (c.R < minR) minR = c.R;
            if (c.R > maxR) maxR = c.R;
            if (c.G < minG) minG = c.G;
            if (c.G > maxG) maxG = c.G;
            if (c.B < minB) minB = c.B;
            if (c.B > maxB) maxB = c.B;
        }
        long dr = maxR - minR, dg = maxG - minG, db = maxB - minB;
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>Exact widest pairwise squared distance. Interior points are skipped once they cannot beat the incumbent.</summary>
    internal static long WidestSquared(Rgb24[] colors)
    {
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        foreach (Rgb24 c in colors)
        {
            if (c.R < minR) minR = c.R;
            if (c.R > maxR) maxR = c.R;
            if (c.G < minG) minG = c.G;
            if (c.G > maxG) maxG = c.G;
            if (c.B < minB) minB = c.B;
            if (c.B > maxB) maxB = c.B;
        }

        long widest = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            Rgb24 a = colors[i];
            long reachR = Math.Max(a.R - minR, maxR - a.R);
            long reachG = Math.Max(a.G - minG, maxG - a.G);
            long reachB = Math.Max(a.B - minB, maxB - a.B);
            if (reachR * reachR + reachG * reachG + reachB * reachB <= widest)
                continue;
            for (int j = i + 1; j < colors.Length; j++)
            {
                long d = DistanceSquared(a, colors[j]);
                if (d > widest)
                    widest = d;
            }
        }
        return widest;
    }

    private static long CellKey(int r, int g, int b) =>
        ((long)(r + 4) << 42) | ((long)(g + 4) << 21) | (long)(b + 4);
}
