using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Renders a per-cell damage heatmap from a diagnostic decode: each data cell is colored by
/// its codeword's ECC correction count — green (clean) through yellow/red (heavily corrected)
/// to dark red (damaged beyond correction). Helps users tune capture distance, focus, and
/// exposure by showing WHERE errors concentrate (glare blob, cursor, screen edge).
/// </summary>
internal sealed class HeatmapRenderer(FastPng png, Interleaver2 interleaver)
{
    private const int CellPx = 6;

    public HeatmapRenderer() : this(new FastPng(), new Interleaver2())
    {
    }

    public void Render(Layout layout, int[] codewordErrors, string outPath)
    {
        int w = layout.GridW * CellPx, h = layout.GridH * CellPx;
        var px = new Rgb24[w * h];
        int cwCount = layout.CodewordCount;
        int bits = layout.BitsPerCell;
        long protectedBytes = (long)cwCount * Fec.CodewordLength;

        // v2 interleave: a cell byte's codeword comes from its CLASSIC position, found by
        // inverting the permutation.
        int[]? inverse = null;
        if (layout.Interleave2)
        {
            int[] perm = interleaver.Permutation((int)protectedBytes);
            inverse = new int[perm.Length];
            for (int i = 0; i < perm.Length; i++)
                inverse[perm[i]] = i;
        }
        // A codeword corrects up to parity/2 bytes — full red at that capacity.
        double capacity = Math.Max(1, layout.EccParity / 2.0);

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                long bitOffset = cellIndex * bits;
                long firstByte = bitOffset >> 3;
                long lastByte = (bitOffset + bits - 1) >> 3;
                int worst = 0;
                bool failed = false;
                for (long b = firstByte; b <= lastByte && b < protectedBytes; b++)
                {
                    long classic = inverse?[(int)b] ?? b;
                    int errors = codewordErrors[(int)(classic % cwCount)];
                    if (errors < 0)
                        failed = true;
                    else
                        worst = Math.Max(worst, errors);
                }

                var color = failed
                    ? new Rgb24(90, 0, 20) // beyond correction
                    : Gradient(Math.Min(1.0, worst / capacity));
                Fill(px, w, gx * CellPx, gy * CellPx, color);
            }
        }
        png.Write(outPath, px, w, h, upFilter: true, System.IO.Compression.CompressionLevel.Fastest);
    }

    /// <summary>
    /// Capture-QUALITY heatmap from per-cell classification margins (squared palette distance of
    /// the winning sample). Unlike <see cref="Render"/> — which needs a successful RS decode — this
    /// renders whenever the frame was merely located, so it shows WHERE a capture is weak (glare,
    /// defocus, edge) even when the decode never completed. Green = confident, red = ambiguous.
    /// It is a quality/ambiguity map, NOT a correctness map: a glare-saturated cell can map
    /// confidently to the wrong color and still read green.
    /// </summary>
    public void RenderQuality(Layout layout, int[] cellMargins, string outPath)
    {
        int w = layout.GridW * CellPx, h = layout.GridH * CellPx;
        var px = new Rgb24[w * h];
        // Mirrors GridSampler's ConfidentDist (200) → green and AbsoluteSuspectDist (4000) → red.
        const double confident = 200, ambiguous = 4000;
        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int margin = cellMargins[(int)cellIndex];
                var color = margin > ambiguous * 4
                    ? new Rgb24(90, 0, 20) // far past any palette color — likely unreadable
                    : Gradient(Math.Clamp((margin - confident) / (ambiguous - confident), 0, 1));
                Fill(px, w, gx * CellPx, gy * CellPx, color);
            }
        }
        png.Write(outPath, px, w, h, upFilter: true, System.IO.Compression.CompressionLevel.Fastest);
    }

    /// <summary>0 → green, 0.5 → yellow, 1 → red.</summary>
    private static Rgb24 Gradient(double t)
    {
        double r = t <= 0.5 ? t * 2 : 1.0;
        double g = t <= 0.5 ? 1.0 : 1.0 - (t - 0.5) * 2;
        return new Rgb24((byte)(40 + r * 190), (byte)(40 + g * 150), 45);
    }

    private static void Fill(Rgb24[] px, int stride, int x, int y, Rgb24 color)
    {
        int first = y * stride + x;
        Array.Fill(px, color, first, CellPx);
        for (int r = 1; r < CellPx; r++)
            Array.Copy(px, first, px, first + r * stride, CellPx);
    }
}
