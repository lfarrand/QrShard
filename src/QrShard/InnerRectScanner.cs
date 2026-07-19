using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Measures the frame's inner (white) edge with subpixel precision.</summary>
internal static class InnerRectScanner
{
    /// <summary>
    /// Scans inward from the frame's outer box to its inner (white) edge with subpixel precision:
    /// the dark-to-light luminance crossing is linearly interpolated, and the median over five
    /// scan lines per side rejects outliers. Precision here directly bounds far-edge cell drift.
    /// </summary>
    public static InnerRect FindInnerRect(Bitmap bmp, PixelRect frame)
    {
        int[] ys = Enumerable.Range(0, 5).Select(i => frame.Y0 + frame.H * (3 + i) / 10).ToArray();
        int[] xs = Enumerable.Range(0, 5).Select(i => frame.X0 + frame.W * (3 + i) / 10).ToArray();

        double x0 = Median(ys.Select(y => EdgeX(frame.X0, +1, y)));
        double x1 = Median(ys.Select(y => EdgeX(frame.X1 - 1, -1, y)));
        double y0 = Median(xs.Select(x => EdgeY(frame.Y0, +1, x)));
        double y1 = Median(xs.Select(x => EdgeY(frame.Y1 - 1, -1, x)));
        if (x1 - x0 < 32 || y1 - y0 < 32)
            throw new ShardDecodeException("Frame interior is too small to decode.");
        return new InnerRect(x0, y0, x1, y1);

        double EdgeX(int start, int dir, int y) =>
            Edge(start, dir, Math.Abs(frame.W), i => Lum(bmp.At(i, y)));

        double EdgeY(int start, int dir, int x) =>
            Edge(start, dir, Math.Abs(frame.H), i => Lum(bmp.At(x, i)));

        // Walks from inside the frame toward the interior until luminance crosses 128,
        // then interpolates the crossing. Returns the subpixel edge coordinate.
        static double Edge(int start, int dir, int limit, Func<int, double> lum)
        {
            int i = start;
            for (int steps = 0; steps < limit; steps++, i += dir)
            {
                double l = lum(i + dir);
                if (l >= 128)
                {
                    double lPrev = lum(i);
                    double frac = l > lPrev ? Math.Clamp((128 - lPrev) / (l - lPrev), 0, 1) : 0.5;
                    // Edge lies between pixel centers i and i+dir; convert to a boundary coordinate.
                    return dir > 0 ? i + 0.5 + frac : i + 0.5 - frac;
                }
            }
            throw new ShardDecodeException("Could not find the frame's inner edge.");
        }

        static double Lum(Rgb24 p) => (p.R + p.G + p.B) / 3.0;

        static double Median(IEnumerable<double> values)
        {
            var v = values.Order().ToArray();
            return v[v.Length / 2];
        }
    }
}
