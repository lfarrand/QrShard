using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Builds the color palette for a given bits-per-cell density. Colors are spread across the RGB
/// cube with maximal per-channel spacing so that nearest-color classification is robust.
/// </summary>
internal sealed class Palette
{
    public const int MinBits = 1;
    public const int MaxBits = 8;

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
}
