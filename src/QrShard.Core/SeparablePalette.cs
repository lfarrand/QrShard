using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// A drop-in replacement for <see cref="Palette.Nearest"/> for palettes that are an exact
/// Cartesian product of per-channel level sets — three table lookups instead of a scan of every
/// entry.
///
/// Why it is exact, not an approximation: squared RGB distance is a sum of three independent
/// per-channel terms, and the palette index is the mixed-radix digit string (iR, iG, iB) — see
/// <see cref="Palette.ChannelCounts"/>. So the minimising set is the product of the three
/// per-channel minimising sets, and because the index map is strictly increasing in lexicographic
/// digit order, the LOWEST minimising index — exactly what the scan's strict `&lt;` returns on a
/// tie — is the concatenation of the lowest per-channel winners. Ties therefore agree by
/// construction, which matters: the classifier's margin drives erasure flagging and its runner-up
/// drives Chase trials.
///
/// The structure is not assumed, it is verified per rebuild. <see cref="Palette.Build"/> emits it,
/// and it survives the gain-like illumination fit that gates the gradient sampling path (a
/// per-channel gain maps a product set to a product set). A measured strip that capture noise has
/// knocked off the grid does not; that palette is classified by <see cref="MeasuredColorIndex"/>.
/// </summary>
internal sealed class SeparablePalette
{
    // Winning per-channel level index for every possible channel value.
    private readonly byte[] _r = new byte[256], _g = new byte[256], _b = new byte[256];
    private int _strideR, _strideG;
    private int _nR, _nG, _nB;
    private long _closestSquared;

    /// <summary>
    /// Re-derives the tables from <paramref name="palette"/>, reusing the buffers. Returns false —
    /// leaving the instance unusable until a later successful rebuild — when the palette is not an
    /// exact product, in which case only the scan is correct.
    /// </summary>
    public bool TryRebuild(Rgb24[] palette)
    {
        int bits = BitOperations.Log2((uint)Math.Max(palette.Length, 1));
        if (palette.Length != 1 << bits || bits is < Palette.MinBits or > Palette.MaxBits)
            return false;

        var (nR, nG, nB) = Palette.ChannelCounts(bits);
        int strideR = nG * nB;
        // Every entry must be the product of the levels its digits select. Reading the candidate
        // levels off the axes and re-checking every entry against them is what makes the fast
        // path safe for a palette measured from a capture rather than built.
        for (int i = 0; i < palette.Length; i++)
        {
            if (palette[i].R != palette[i / strideR * strideR].R
                || palette[i].G != palette[i / nB % nG * nB].G
                || palette[i].B != palette[i % nB].B)
                return false;
        }

        _strideR = strideR;
        _strideG = nB;
        _nR = nR;
        _nG = nG;
        _nB = nB;
        // A product's closest pair sits on one channel with the other two held equal, so the
        // minimum level gap is the exact minimum squared distance. No spatial hash.
        _closestSquared = ChannelClosest(palette, nR, strideR, 0);
        _closestSquared = Math.Min(_closestSquared, ChannelClosest(palette, nG, nB, 1));
        _closestSquared = Math.Min(_closestSquared, ChannelClosest(palette, nB, 1, 2));
        FillR(_r, palette, nR, strideR);
        FillG(_g, palette, nG, nB);
        FillB(_b, palette, nB);
        return true;
    }

    /// <summary>
    /// Minimum pairwise squared distance. Valid only after <see cref="TryRebuild"/> returned true.
    /// </summary>
    public long ClosestSquared => _closestSquared;

    /// <summary>Index of the palette color nearest the sample — identical to the scan's answer.</summary>
    public int Nearest(int r, int g, int b) => _r[r] * _strideR + _g[g] * _strideG + _b[b];

    /// <summary>
    /// Runner-up for a product palette. The nearest colour is the product of the per-channel
    /// winners, so the next one is either another tied level (same distance, next index) or a
    /// single channel stepped to its next-best level. Matches <see cref="Palette.SecondNearest"/>,
    /// including the lowest-index tie.
    /// </summary>
    public int SecondNearest(Rgb24[] palette, int r, int g, int b, int excludeIndex, out long distance)
    {
        int iR = _r[r], iG = _g[g], iB = _b[b];
        Span<int> tiedR = stackalloc int[16];
        Span<int> tiedG = stackalloc int[16];
        Span<int> tiedB = stackalloc int[16];
        int nR = TiedLevels(palette, r, 0, _nR, _strideR, iR, tiedR);
        int nG = TiedLevels(palette, g, 1, _nG, _strideG, iG, tiedG);
        int nB = TiedLevels(palette, b, 2, _nB, 1, iB, tiedB);

        // B is the fastest digit, so the first combination that is not the excluded index is the
        // lowest other index at the winning distance.
        for (int tr = 0; tr < nR; tr++)
        {
            for (int tg = 0; tg < nG; tg++)
            {
                for (int tb = 0; tb < nB; tb++)
                {
                    int idx = tiedR[tr] * _strideR + tiedG[tg] * _strideG + tiedB[tb];
                    if (idx == excludeIndex)
                        continue;
                    distance = Distance(palette, idx, r, g, b);
                    return idx;
                }
            }
        }

        long bestDist = long.MaxValue;
        int best = excludeIndex;
        Consider(palette, r, g, b, NextWorse(palette, r, 0, _nR, _strideR, iR), iG, iB, excludeIndex, ref bestDist, ref best);
        Consider(palette, r, g, b, iR, NextWorse(palette, g, 1, _nG, _strideG, iG), iB, excludeIndex, ref bestDist, ref best);
        Consider(palette, r, g, b, iR, iG, NextWorse(palette, b, 2, _nB, 1, iB), excludeIndex, ref bestDist, ref best);
        distance = bestDist;
        return best;
    }

    private int TiedLevels(Rgb24[] palette, int sample, int channel, int count, int stride, int winner, Span<int> tied)
    {
        int winnerSq = Sq(sample, Level(palette, channel, winner, stride));
        int n = 0;
        for (int k = 0; k < count; k++)
        {
            if (Sq(sample, Level(palette, channel, k, stride)) == winnerSq)
                tied[n++] = k;
        }
        return n;
    }

    /// <summary>Lowest level whose squared distance is strictly worse than the winner's.</summary>
    private int NextWorse(Rgb24[] palette, int sample, int channel, int count, int stride, int winner)
    {
        int winnerSq = Sq(sample, Level(palette, channel, winner, stride));
        int bestSq = int.MaxValue;
        int best = -1;
        for (int k = 0; k < count; k++)
        {
            int sq = Sq(sample, Level(palette, channel, k, stride));
            if (sq <= winnerSq)
                continue;
            if (sq < bestSq || (sq == bestSq && k < best))
            {
                bestSq = sq;
                best = k;
            }
        }
        return best;
    }

    private void Consider(Rgb24[] palette, int r, int g, int b, int iR, int iG, int iB, int exclude,
        ref long bestDist, ref int best)
    {
        if (iR < 0 || iG < 0 || iB < 0)
            return;
        int idx = iR * _strideR + iG * _strideG + iB;
        if (idx == exclude)
            return;
        long d = Distance(palette, idx, r, g, b);
        if (d < bestDist || (d == bestDist && idx < best))
        {
            bestDist = d;
            best = idx;
        }
    }

    private static int Level(Rgb24[] palette, int channel, int k, int stride)
    {
        Rgb24 colour = palette[k * stride];
        return channel switch
        {
            0 => colour.R,
            1 => colour.G,
            _ => colour.B,
        };
    }

    private static int Sq(int sample, int level)
    {
        int d = sample - level;
        return d * d;
    }

    private static long Distance(Rgb24[] palette, int index, int r, int g, int b)
    {
        Rgb24 colour = palette[index];
        long dr = r - colour.R, dg = g - colour.G, db = b - colour.B;
        return dr * dr + dg * dg + db * db;
    }

    private static int ChannelLevel(Rgb24 color, int channel) => channel switch
    {
        0 => color.R,
        1 => color.G,
        _ => color.B,
    };

    private static long ChannelClosest(Rgb24[] palette, int count, int stride, int channel)
    {
        if (count < 2)
            return long.MaxValue;
        long best = long.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int a = ChannelLevel(palette[i * stride], channel);
            for (int j = i + 1; j < count; j++)
            {
                long d = a - ChannelLevel(palette[j * stride], channel);
                long sq = d * d;
                if (sq < best)
                    best = sq;
                if (best == 0)
                    return 0;
            }
        }
        return best;
    }

    // The three fills differ only in which channel they read; kept separate so the inner loop
    // stays a straight-line comparison rather than a per-level channel switch.
    private static void FillR(byte[] table, Rgb24[] palette, int count, int stride)
    {
        for (int v = 0; v < 256; v++)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int k = 0; k < count; k++)
            {
                int d = v - palette[k * stride].R;
                d *= d;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }
            table[v] = (byte)best;
        }
    }

    private static void FillG(byte[] table, Rgb24[] palette, int count, int stride)
    {
        for (int v = 0; v < 256; v++)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int k = 0; k < count; k++)
            {
                int d = v - palette[k * stride].G;
                d *= d;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }
            table[v] = (byte)best;
        }
    }

    private static void FillB(byte[] table, Rgb24[] palette, int count)
    {
        for (int v = 0; v < 256; v++)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int k = 0; k < count; k++)
            {
                int d = v - palette[k].B;
                d *= d;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = k;
                }
            }
            table[v] = (byte)best;
        }
    }
}
