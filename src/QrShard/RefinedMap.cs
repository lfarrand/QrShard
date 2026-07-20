namespace QrShard;

/// <summary>
/// Homography plus a Coons-patch field interpolated from the four traced sides: geometric
/// residual (dx, dy) and local black/white colors, evaluated per canvas pixel.
///
/// Evaluation is row-oriented and thread-safe: <see cref="CreateRow"/> returns a small object
/// holding every v-dependent term (left/right side values, corner blends), so per-pixel work
/// is a handful of lerps with no shared mutable state — the rectification warps parallelize
/// over rows. The constructor hoists the per-channel boundary arrays that the original
/// implementation rebuilt (and heap-allocated) for every pixel.
/// </summary>
internal sealed class RefinedMap
{
    private readonly CameraMath _math;
    private readonly Homography _h;
    private readonly double _x0, _y0, _x1, _y1;
    private readonly SideTrace _top, _bottom, _left, _right;

    // Boundary samples reorganized as [channel][sample] for black/white on each side.
    private readonly double[][] _topBlack, _bottomBlack, _leftBlack, _rightBlack;
    private readonly double[][] _topWhite, _bottomWhite, _leftWhite, _rightWhite;

    public RefinedMap(CameraMath math, Homography h, double x0, double y0, double x1, double y1,
        SideTrace top, SideTrace bottom, SideTrace left, SideTrace right)
    {
        _math = math;
        _h = h;
        _x0 = x0;
        _y0 = y0;
        _x1 = x1;
        _y1 = y1;
        _top = top;
        _bottom = bottom;
        _left = left;
        _right = right;
        _topBlack = ByChannel(top.Black);
        _bottomBlack = ByChannel(bottom.Black);
        _leftBlack = ByChannel(left.Black);
        _rightBlack = ByChannel(right.Black);
        _topWhite = ByChannel(top.White);
        _bottomWhite = ByChannel(bottom.White);
        _leftWhite = ByChannel(left.White);
        _rightWhite = ByChannel(right.White);
    }

    /// <summary>Cached v-dependent terms for one canvas row.</summary>
    internal sealed class Row
    {
        internal double V;
        internal double DxLeft, DxRight, DyLeft, DyRight;
        internal readonly double[] LeftBlack = new double[3], RightBlack = new double[3];
        internal readonly double[] LeftWhite = new double[3], RightWhite = new double[3];
        internal readonly double[] CornerABlack = new double[3], CornerBBlack = new double[3];
        internal readonly double[] CornerAWhite = new double[3], CornerBWhite = new double[3];
    }

    private static double[][] ByChannel(double[][] samples)
    {
        var channels = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            channels[c] = new double[SideTrace.SamplesPerSide];
            for (int i = 0; i < SideTrace.SamplesPerSide; i++)
                channels[c][i] = samples[i][c];
        }
        return channels;
    }

    public Row CreateRow(double y)
    {
        var row = new Row { V = Math.Clamp((y - _y0) / (_y1 - _y0), 0, 1) };
        double v = row.V;
        row.DxLeft = SideValue(_left.Dx, v);
        row.DxRight = SideValue(_right.Dx, v);
        row.DyLeft = SideValue(_left.Dy, v);
        row.DyRight = SideValue(_right.Dy, v);
        for (int c = 0; c < 3; c++)
        {
            row.LeftBlack[c] = SideValue(_leftBlack[c], v);
            row.RightBlack[c] = SideValue(_rightBlack[c], v);
            row.LeftWhite[c] = SideValue(_leftWhite[c], v);
            row.RightWhite[c] = SideValue(_rightWhite[c], v);
            // Corner blend split by u-weight: (1-u)*A + u*B, with the v-weights folded into A/B.
            double c00B = (_topBlack[c][0] + _leftBlack[c][0]) / 2;
            double c10B = (_topBlack[c][^1] + _rightBlack[c][0]) / 2;
            double c01B = (_bottomBlack[c][0] + _leftBlack[c][^1]) / 2;
            double c11B = (_bottomBlack[c][^1] + _rightBlack[c][^1]) / 2;
            row.CornerABlack[c] = (1 - v) * c00B + v * c01B;
            row.CornerBBlack[c] = (1 - v) * c10B + v * c11B;
            double c00W = (_topWhite[c][0] + _leftWhite[c][0]) / 2;
            double c10W = (_topWhite[c][^1] + _rightWhite[c][0]) / 2;
            double c01W = (_bottomWhite[c][0] + _leftWhite[c][^1]) / 2;
            double c11W = (_bottomWhite[c][^1] + _rightWhite[c][^1]) / 2;
            row.CornerAWhite[c] = (1 - v) * c00W + v * c01W;
            row.CornerBWhite[c] = (1 - v) * c10W + v * c11W;
        }
        return row;
    }

    /// <summary>Canvas-to-photo mapping for a pixel in the given row.</summary>
    public (double X, double Y) Apply(Row row, double x, double y)
    {
        var (px, py) = _h.Apply(x, y);
        double u = Math.Clamp((x - _x0) / (_x1 - _x0), 0, 1);
        double v = row.V;

        // Each traced side only observes displacement along its own normal (the aperture
        // problem: an edge cannot reveal tangential shift). So the top/bottom pair lofts
        // one orthogonal component of the photo-space correction and the left/right pair
        // the other, and the two families simply add — classical Coons corner-blending
        // would wrongly average a measured component with a structurally-zero one.
        double dx = (1 - v) * SideValue(_top.Dx, u) + v * SideValue(_bottom.Dx, u)
                  + (1 - u) * row.DxLeft + u * row.DxRight;
        double dy = (1 - v) * SideValue(_top.Dy, u) + v * SideValue(_bottom.Dy, u)
                  + (1 - u) * row.DyLeft + u * row.DyRight;
        return (px + dx, py + dy);
    }

    /// <summary>Fills the interpolated black/white reference colors (3 channels each) — no allocations.</summary>
    public void Illumination(Row row, double x, Span<double> black, Span<double> white)
    {
        double u = Math.Clamp((x - _x0) / (_x1 - _x0), 0, 1);
        double v = row.V;
        for (int c = 0; c < 3; c++)
        {
            double tB = SideValue(_topBlack[c], u), bB = SideValue(_bottomBlack[c], u);
            black[c] = (1 - v) * tB + v * bB + (1 - u) * row.LeftBlack[c] + u * row.RightBlack[c]
                     - ((1 - u) * row.CornerABlack[c] + u * row.CornerBBlack[c]);
            double tW = SideValue(_topWhite[c], u), bW = SideValue(_bottomWhite[c], u);
            white[c] = (1 - v) * tW + v * bW + (1 - u) * row.LeftWhite[c] + u * row.RightWhite[c]
                     - ((1 - u) * row.CornerAWhite[c] + u * row.CornerBWhite[c]);
        }
    }

    private double SideValue(double[] samples, double t)
    {
        double pos = t * SideTrace.SamplesPerSide - 0.5;
        int i0 = Math.Clamp((int)Math.Floor(pos), 0, SideTrace.SamplesPerSide - 1);
        int i1 = Math.Min(i0 + 1, SideTrace.SamplesPerSide - 1);
        return _math.Lerp(samples[i0], samples[i1], Math.Clamp(pos - i0, 0, 1));
    }
}
