using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Renders a per-cell damage heatmap from a diagnostic decode: each data cell is colored by
/// its codeword's ECC correction count — green (clean) through yellow/red (heavily corrected)
/// to dark red (damaged beyond correction). Helps users tune capture distance, focus, and
/// exposure by showing WHERE errors concentrate (glare blob, cursor, screen edge).
/// </summary>
internal sealed class HeatmapRenderer(FastPng png)
{
    private const int CellPx = 6;

    public HeatmapRenderer() : this(new FastPng())
    {
    }

    public void Render(Layout layout, int[] codewordErrors, string outPath)
    {
        int w = layout.GridW * CellPx, h = layout.GridH * CellPx;
        var px = new Rgb24[w * h];
        int cwCount = layout.CodewordCount;
        int bits = layout.BitsPerCell;
        long protectedBytes = (long)cwCount * Fec.CodewordLength;
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
                    int errors = codewordErrors[(int)(b % cwCount)];
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
