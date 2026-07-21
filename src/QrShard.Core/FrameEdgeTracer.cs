namespace QrShard;

/// <summary>Per-side traced data: normal-direction residuals plus local black/white colors.</summary>
internal sealed class SideTrace
{
    /// <summary>Trace points per frame side.</summary>
    public const int SamplesPerSide = 17;

    public readonly double[] Dx = new double[SamplesPerSide];
    public readonly double[] Dy = new double[SamplesPerSide];
    public readonly double[][] Black = new double[SamplesPerSide][];
    public readonly double[][] White = new double[SamplesPerSide][];
    public readonly bool[] Valid = new bool[SamplesPerSide];
}

/// <summary>
/// Traces the frame's edges in the original photo with subpixel precision, capturing per-point
/// geometric residuals against the homography plus local black/white reference colors.
/// </summary>
internal sealed class FrameEdgeTracer(CameraMath math) : IFrameEdgeTracer
{
    public FrameEdgeTracer() : this(new CameraMath())
    {
    }

    public SideTrace? TraceSide(Bitmap photo, CanvasGeometry geometry,
        Func<int, (double X, double Y)> canvasPoint, (double X, double Y) outwardNormal)
    {
        var trace = new SideTrace();
        int valid = 0;
        for (int i = 0; i < SideTrace.SamplesPerSide; i++)
        {
            var p = canvasPoint(i);
            var basePt = geometry.H.Apply(p.X, p.Y);
            var stepPt = geometry.H.Apply(p.X + outwardNormal.X * 4, p.Y + outwardNormal.Y * 4);
            double dx = stepPt.X - basePt.X, dy = stepPt.Y - basePt.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
                continue;
            dx /= len;
            dy /= len;

            if (TraceEdge(photo, basePt, (dx, dy), geometry.Module, out double outerT, out double thickness))
            {
                trace.Dx[i] = dx * outerT;
                trace.Dy[i] = dy * outerT;
                double blackT = outerT - thickness / 2;  // mid-frame: guaranteed black
                double whiteT = outerT + thickness / 2;  // quiet zone just outside: guaranteed white
                trace.Black[i] = SamplePatch(photo, basePt.X + dx * blackT, basePt.Y + dy * blackT);
                trace.White[i] = SamplePatch(photo, basePt.X + dx * whiteT, basePt.Y + dy * whiteT);
                trace.Valid[i] = true;
                valid++;
            }
        }
        if (valid < SideTrace.SamplesPerSide * 0.7)
            return null;

        FillInvalid(trace);
        return trace;
    }

    /// <summary>
    /// Walks a luminance profile along the outward normal and finds the frame's outer edge
    /// (light-to-dark moving inward) with subpixel precision, plus the frame thickness.
    /// </summary>
    private static bool TraceEdge(Bitmap photo, (double X, double Y) basePt, (double X, double Y) dir,
        double module, out double outerT, out double thickness)
    {
        outerT = 0;
        thickness = 0;
        double search = Math.Max(12, module * 3);
        const double step = 0.5;
        int samples = (int)(2 * search / step) + 1;

        Span<double> lum = stackalloc double[512];
        if (samples > lum.Length)
            samples = lum.Length;
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < samples; i++)
        {
            double t = search - i * step; // from outside (+search) inward (-search)
            var c = photo.SampleBilinear(basePt.X + dir.X * t - 0.5, basePt.Y + dir.Y * t - 0.5);
            lum[i] = (c.R + c.G + c.B) / 3.0;
            min = Math.Min(min, lum[i]);
            max = Math.Max(max, lum[i]);
        }
        if (max - min < 50)
            return false;
        double mid = (min + max) / 2;

        // Outer edge: first crossing below mid, walking inward.
        int outer = -1;
        for (int i = 1; i < samples; i++)
        {
            if (lum[i - 1] >= mid && lum[i] < mid)
            {
                outer = i;
                break;
            }
        }
        if (outer < 0)
            return false;
        double frac = (lum[outer - 1] - mid) / (lum[outer - 1] - lum[outer]);
        outerT = search - (outer - 1 + frac) * step;

        // Inner edge: next crossing back above mid.
        int inner = -1;
        for (int i = outer + 1; i < samples; i++)
        {
            if (lum[i - 1] < mid && lum[i] >= mid)
            {
                inner = i;
                break;
            }
        }
        if (inner < 0)
            return false;
        double innerFrac = (mid - lum[inner - 1]) / (lum[inner] - lum[inner - 1]);
        double innerT = search - (inner - 1 + innerFrac) * step;

        thickness = outerT - innerT;
        return thickness >= 2.5 && thickness <= search;
    }

    private static double[] SamplePatch(Bitmap photo, double cx, double cy)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        int x0 = (int)Math.Round(cx), y0 = (int)Math.Round(cy);
        for (int y = y0 - 1; y <= y0 + 1; y++)
        {
            for (int x = x0 - 1; x <= x0 + 1; x++)
            {
                int xc = Math.Clamp(x, 0, photo.Width - 1), yc = Math.Clamp(y, 0, photo.Height - 1);
                var p = photo.At(xc, yc);
                r += p.R;
                g += p.G;
                b += p.B;
                n++;
            }
        }
        return [r / n, g / n, b / n];
    }

    private void FillInvalid(SideTrace trace)
    {
        for (int i = 0; i < SideTrace.SamplesPerSide; i++)
        {
            if (trace.Valid[i])
                continue;
            int prev = i - 1, next = i + 1;
            while (prev >= 0 && !trace.Valid[prev])
                prev--;
            while (next < SideTrace.SamplesPerSide && !trace.Valid[next])
                next++;
            int source = prev >= 0 ? prev : next;
            int other = next < SideTrace.SamplesPerSide ? next : source;
            double t = prev >= 0 && next < SideTrace.SamplesPerSide ? (double)(i - prev) / (next - prev) : 0;
            trace.Dx[i] = math.Lerp(trace.Dx[source], trace.Dx[other], t);
            trace.Dy[i] = math.Lerp(trace.Dy[source], trace.Dy[other], t);
            trace.Black[i] = LerpVec(trace.Black[source], trace.Black[other], t);
            trace.White[i] = LerpVec(trace.White[source], trace.White[other], t);
        }

        double[] LerpVec(double[] a, double[] b, double t) =>
            [math.Lerp(a[0], b[0], t), math.Lerp(a[1], b[1], t), math.Lerp(a[2], b[2], t)];
    }
}
