namespace QrShard;

/// <summary>
/// Homography plus a Coons-patch field interpolated from the four traced sides: geometric
/// residual (dx, dy) and local black/white colors, evaluated per canvas pixel.
///
/// Evaluation is row-oriented: <see cref="BeginRow"/> caches every v-dependent term (the
/// left/right side values and the corner blends), so the per-pixel work is a handful of lerps
/// with zero allocations — the constructor hoists the per-channel boundary arrays that the old
/// implementation rebuilt (and heap-allocated) for every pixel.
/// </summary>
internal sealed class RefinedMap
{
    private readonly CameraMath _math;
    private readonly Homography _h;
    private readonly double _x0, _y0, _x1, _y1;
    private readonly SideTrace _top, _bottom;

    // Boundary samples reorganized as [channel][sample] for black/white on each side.
    private readonly double[][] _topBlack, _bottomBlack, _leftBlack, _rightBlack;
    private readonly double[][] _topWhite, _bottomWhite, _leftWhite, _rightWhite;

    // v-dependent terms cached by BeginRow.
    private double _v;
    private readonly double[] _rowLeftBlack = new double[3], _rowRightBlack = new double[3];
    private readonly double[] _rowLeftWhite = new double[3], _rowRightWhite = new double[3];
    private readonly double[] _cornerABlack = new double[3], _cornerBBlack = new double[3];
    private readonly double[] _cornerAWhite = new double[3], _cornerBWhite = new double[3];
    private double _rowDxLeft, _rowDxRight, _rowDyLeft, _rowDyRight;

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
        LeftRight = (left, right);
        _topBlack = ByChannel(top.Black);
        _bottomBlack = ByChannel(bottom.Black);
        _leftBlack = ByChannel(left.Black);
        _rightBlack = ByChannel(right.Black);
        _topWhite = ByChannel(top.White);
        _bottomWhite = ByChannel(bottom.White);
        _leftWhite = ByChannel(left.White);
        _rightWhite = ByChannel(right.White);
    }

    private (SideTrace Left, SideTrace Right) LeftRight { get; }

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

    /// <summary>Caches every v-dependent interpolation term for the given canvas row.</summary>
    public void BeginRow(double y)
    {
        var (left, right) = LeftRight;
        _v = Math.Clamp((y - _y0) / (_y1 - _y0), 0, 1);
        _rowDxLeft = SideValue(left.Dx, _v);
        _rowDxRight = SideValue(right.Dx, _v);
        _rowDyLeft = SideValue(left.Dy, _v);
        _rowDyRight = SideValue(right.Dy, _v);
        for (int c = 0; c < 3; c++)
        {
            _rowLeftBlack[c] = SideValue(_leftBlack[c], _v);
            _rowRightBlack[c] = SideValue(_rightBlack[c], _v);
            _rowLeftWhite[c] = SideValue(_leftWhite[c], _v);
            _rowRightWhite[c] = SideValue(_rightWhite[c], _v);
            // Corner blend, split by u-weight: (1-u)*A + u*B where A/B fold in the v-weights.
            double c00B = (_topBlack[c][0] + _leftBlack[c][0]) / 2;
            double c10B = (_topBlack[c][^1] + _rightBlack[c][0]) / 2;
            double c01B = (_bottomBlack[c][0] + _leftBlack[c][^1]) / 2;
            double c11B = (_bottomBlack[c][^1] + _rightBlack[c][^1]) / 2;
            _cornerABlack[c] = (1 - _v) * c00B + _v * c01B;
            _cornerBBlack[c] = (1 - _v) * c10B + _v * c11B;
            double c00W = (_topWhite[c][0] + _leftWhite[c][0]) / 2;
            double c10W = (_topWhite[c][^1] + _rightWhite[c][0]) / 2;
            double c01W = (_bottomWhite[c][0] + _leftWhite[c][^1]) / 2;
            double c11W = (_bottomWhite[c][^1] + _rightWhite[c][^1]) / 2;
            _cornerAWhite[c] = (1 - _v) * c00W + _v * c01W;
            _cornerBWhite[c] = (1 - _v) * c10W + _v * c11W;
        }
    }

    /// <summary>Canvas-to-photo mapping for a pixel in the row started by <see cref="BeginRow"/>.</summary>
    public (double X, double Y) Apply(double x, double y)
    {
        var (px, py) = _h.Apply(x, y);
        double u = Math.Clamp((x - _x0) / (_x1 - _x0), 0, 1);

        // Each traced side only observes displacement along its own normal (the aperture
        // problem: an edge cannot reveal tangential shift). So the top/bottom pair lofts
        // one orthogonal component of the photo-space correction and the left/right pair
        // the other, and the two families simply add — classical Coons corner-blending
        // would wrongly average a measured component with a structurally-zero one.
        double dx = (1 - _v) * SideValue(_top.Dx, u) + _v * SideValue(_bottom.Dx, u)
                  + (1 - u) * _rowDxLeft + u * _rowDxRight;
        double dy = (1 - _v) * SideValue(_top.Dy, u) + _v * SideValue(_bottom.Dy, u)
                  + (1 - u) * _rowDyLeft + u * _rowDyRight;
        return (px + dx, py + dy);
    }

    /// <summary>Fills the interpolated black/white reference colors (3 channels each) for a pixel
    /// in the row started by <see cref="BeginRow"/> — no allocations.</summary>
    public void Illumination(double x, Span<double> black, Span<double> white)
    {
        double u = Math.Clamp((x - _x0) / (_x1 - _x0), 0, 1);
        for (int c = 0; c < 3; c++)
        {
            double tB = SideValue(_topBlack[c], u), bB = SideValue(_bottomBlack[c], u);
            black[c] = (1 - _v) * tB + _v * bB + (1 - u) * _rowLeftBlack[c] + u * _rowRightBlack[c]
                     - ((1 - u) * _cornerABlack[c] + u * _cornerBBlack[c]);
            double tW = SideValue(_topWhite[c], u), bW = SideValue(_bottomWhite[c], u);
            white[c] = (1 - _v) * tW + _v * bW + (1 - u) * _rowLeftWhite[c] + u * _rowRightWhite[c]
                     - ((1 - u) * _cornerAWhite[c] + u * _cornerBWhite[c]);
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
