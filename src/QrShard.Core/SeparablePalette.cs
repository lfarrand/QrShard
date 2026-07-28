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
/// knocked off the grid does not, and then the caller must keep scanning.
/// </summary>
internal sealed class SeparablePalette
{
    // Winning per-channel level index for every possible channel value.
    private readonly byte[] _r = new byte[256], _g = new byte[256], _b = new byte[256];
    private int _strideR, _strideG;

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
        FillR(_r, palette, nR, strideR);
        FillG(_g, palette, nG, nB);
        FillB(_b, palette, nB);
        return true;
    }

    /// <summary>Index of the palette color nearest the sample — identical to the scan's answer.</summary>
    public int Nearest(int r, int g, int b) => _r[r] * _strideR + _g[g] * _strideG + _b[b];

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
