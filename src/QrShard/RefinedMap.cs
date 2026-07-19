namespace QrShard;

/// <summary>
/// Homography plus a Coons-patch field interpolated from the four traced sides: geometric
/// residual (dx, dy) and local black/white colors, evaluated per canvas pixel.
/// </summary>
internal sealed class RefinedMap(CameraMath math, Homography h, double x0, double y0, double x1, double y1,
    SideTrace top, SideTrace bottom, SideTrace left, SideTrace right)
{
    public (double X, double Y) Apply(double x, double y)
    {
        var (px, py) = h.Apply(x, y);
        double u = Math.Clamp((x - x0) / (x1 - x0), 0, 1);
        double v = Math.Clamp((y - y0) / (y1 - y0), 0, 1);

        // Each traced side only observes displacement along its own normal (the aperture
        // problem: an edge cannot reveal tangential shift). So the top/bottom pair lofts
        // one orthogonal component of the photo-space correction and the left/right pair
        // the other, and the two families simply add — classical Coons corner-blending
        // would wrongly average a measured component with a structurally-zero one.
        double dx = (1 - v) * SideValue(top.Dx, u) + v * SideValue(bottom.Dx, u)
                  + (1 - u) * SideValue(left.Dx, v) + u * SideValue(right.Dx, v);
        double dy = (1 - v) * SideValue(top.Dy, u) + v * SideValue(bottom.Dy, u)
                  + (1 - u) * SideValue(left.Dy, v) + u * SideValue(right.Dy, v);
        return (px + dx, py + dy);
    }

    public (double[] Black, double[] White) Illumination(double x, double y)
    {
        double u = Math.Clamp((x - x0) / (x1 - x0), 0, 1);
        double v = Math.Clamp((y - y0) / (y1 - y0), 0, 1);
        var black = new double[3];
        var white = new double[3];
        for (int c = 0; c < 3; c++)
        {
            black[c] = Coons(u, v, Channel(top.Black, c), Channel(bottom.Black, c), Channel(left.Black, c), Channel(right.Black, c));
            white[c] = Coons(u, v, Channel(top.White, c), Channel(bottom.White, c), Channel(left.White, c), Channel(right.White, c));
        }
        return (black, white);

        static double[] Channel(double[][] side, int c)
        {
            var values = new double[SideTrace.SamplesPerSide];
            for (int i = 0; i < SideTrace.SamplesPerSide; i++)
                values[i] = side[i][c];
            return values;
        }
    }

    /// <summary>Transfinite (Coons) interpolation from four boundary sample arrays.</summary>
    private double Coons(double u, double v, double[] top, double[] bottom, double[] left, double[] right)
    {
        double t = SideValue(top, u), b = SideValue(bottom, u);
        double l = SideValue(left, v), r = SideValue(right, v);
        double c00 = (top[0] + left[0]) / 2;
        double c10 = (top[^1] + right[0]) / 2;
        double c01 = (bottom[0] + left[^1]) / 2;
        double c11 = (bottom[^1] + right[^1]) / 2;
        return (1 - v) * t + v * b + (1 - u) * l + u * r
             - ((1 - u) * (1 - v) * c00 + u * (1 - v) * c10 + (1 - u) * v * c01 + u * v * c11);
    }

    private double SideValue(double[] samples, double t)
    {
        double pos = t * SideTrace.SamplesPerSide - 0.5;
        int i0 = Math.Clamp((int)Math.Floor(pos), 0, SideTrace.SamplesPerSide - 1);
        int i1 = Math.Min(i0 + 1, SideTrace.SamplesPerSide - 1);
        return math.Lerp(samples[i0], samples[i1], Math.Clamp(pos - i0, 0, 1));
    }
}
