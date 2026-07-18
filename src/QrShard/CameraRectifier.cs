using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Camera-capture front-end: finds the four QR-style finder patterns that camera-profile
/// shards carry in their top/bottom bands, resolves orientation via the tick mark next to the
/// top-left finder, solves the 4-point homography, and resamples the photo into an
/// axis-aligned canvas that the ordinary decode pipeline can consume.
///
/// Phase 1 handles rotation (any angle, including 90/180/270), perspective from off-axis
/// shots, and the mild blur/JPEG artifacts of a real photo.
///
/// Phase 2 refines the pure homography for handheld reality, using the black frame itself as
/// a dense alignment structure (no encoder/protocol change needed):
///  - the frame's four edges are traced at many points in the photo with subpixel precision;
///    the normal-direction residuals against the homography's prediction feed a Coons-patch
///    correction field that absorbs lens distortion and mild screen curvature;
///  - at each traced point the frame's black and the gutter's white are sampled, and the
///    interpolated fields normalize every rectified pixel per channel — flattening vignette,
///    glare gradients, and white-balance shifts before the color classifier ever sees them.
/// If refinement cannot lock onto the frame, the phase-1 homography result is used as-is.
/// </summary>
internal static class CameraRectifier
{
    private const int MaxCanvasDimension = 12000;

    /// <summary>Rectified bitmap, or null when the image carries no detectable finder patterns.</summary>
    public static Bitmap? TryRectify(Bitmap photo)
    {
        bool[] dark = AdaptiveThreshold(photo);
        var clusters = FindFinderCandidates(photo, dark);
        if (clusters.Count < 4)
            return null;

        var quad = ChooseQuad(clusters);
        if (quad is null)
            return null;

        var oriented = ResolveOrientation(photo, dark, quad);
        if (oriented is null)
            return null;

        var geometry = BuildGeometry(oriented);
        var coarse = WarpHomography(photo, geometry);
        return TryRefine(photo, coarse, geometry) ?? coarse;
    }

    // ---------- Binarization ----------

    /// <summary>
    /// Integral-image adaptive threshold: photos have illumination gradients (screen falloff,
    /// glare) that defeat a single global threshold.
    /// </summary>
    private static bool[] AdaptiveThreshold(Bitmap photo)
    {
        int w = photo.Width, h = photo.Height;
        var lum = new byte[w * h];
        for (int i = 0; i < lum.Length; i++)
        {
            var p = photo.Px[i];
            lum[i] = (byte)((p.R + p.G + p.B) / 3);
        }

        var integral = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += lum[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + rowSum;
            }
        }

        int window = Math.Max(15, Math.Min(w, h) / 16);
        var dark = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - window), y1 = Math.Min(h, y + window);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - window), x1 = Math.Min(w, x + window);
                long sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1]
                         - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
                long area = (long)(x1 - x0) * (y1 - y0);
                dark[y * w + x] = lum[y * w + x] * area * 5 < sum * 4; // below 80% of local mean
            }
        }
        return dark;
    }

    // ---------- Finder pattern detection ----------

    private sealed class Cluster
    {
        public double SumX, SumY, SumModule;
        public int Count;
        public double X => SumX / Count;
        public double Y => SumY / Count;
        public double Module => SumModule / Count;
    }

    /// <summary>
    /// Scans rows for the finder's 1:1:3:1:1 dark/light run signature, verifies each hit
    /// vertically through its center, and clusters agreeing hits. The minimum-module floor
    /// rejects the (much smaller) data cells.
    /// </summary>
    private static List<Cluster> FindFinderCandidates(Bitmap photo, bool[] dark)
    {
        int w = photo.Width, h = photo.Height;
        double minModule = Math.Max(3.0, Math.Min(w, h) / 400.0);
        var clusters = new List<Cluster>();

        var runStarts = new List<int>(64);
        var runDark = new List<bool>(64);
        for (int y = 0; y < h; y += 2)
        {
            runStarts.Clear();
            runDark.Clear();
            int row = y * w;
            bool current = dark[row];
            runStarts.Add(0);
            runDark.Add(current);
            for (int x = 1; x < w; x++)
            {
                if (dark[row + x] != current)
                {
                    current = dark[row + x];
                    runStarts.Add(x);
                    runDark.Add(current);
                }
            }
            runStarts.Add(w);

            for (int r = 0; r + 4 < runDark.Count; r++)
            {
                if (!runDark[r])
                    continue; // pattern starts dark
                double l0 = runStarts[r + 1] - runStarts[r];
                double l1 = runStarts[r + 2] - runStarts[r + 1];
                double l2 = runStarts[r + 3] - runStarts[r + 2];
                double l3 = runStarts[r + 4] - runStarts[r + 3];
                double l4 = runStarts[r + 5] - runStarts[r + 4];
                double module = (l0 + l1 + l2 + l3 + l4) / 7.0;
                if (module < minModule)
                    continue;
                if (!Fits(l0, module) || !Fits(l1, module) || !FitsCenter(l2, module) || !Fits(l3, module) || !Fits(l4, module))
                    continue;

                double cx = runStarts[r + 2] + l2 / 2.0;
                if (VerifyVertical(dark, w, h, (int)cx, y, module, out double cy))
                    Add(clusters, cx, cy, module);
            }
        }
        return clusters;

        static bool Fits(double len, double module) => len >= module * 0.45 && len <= module * 1.8;
        static bool FitsCenter(double len, double module) => len >= module * 2.1 && len <= module * 4.2;
    }

    /// <summary>Checks the 1:1:3:1:1 signature vertically through (x, y) and finds the true center row.</summary>
    private static bool VerifyVertical(bool[] dark, int w, int h, int x, int y, double module, out double cy)
    {
        cy = 0;
        if (!dark[y * w + x])
            return false;

        int top = y, bottom = y;
        while (top > 0 && dark[(top - 1) * w + x])
            top--;
        while (bottom < h - 1 && dark[(bottom + 1) * w + x])
            bottom++;
        double center = bottom - top + 1;
        if (center < module * 2.1 || center > module * 4.2)
            return false;

        if (!RunOutward(dark, w, h, x, top, -1, module) || !RunOutward(dark, w, h, x, bottom, +1, module))
            return false;

        cy = (top + bottom) / 2.0;
        return true;

        // From just past the center run: expect ~1 module light then ~1 module dark.
        static bool RunOutward(bool[] dark, int w, int h, int x, int edge, int dir, double module)
        {
            int i = edge + dir, light = 0, darkRun = 0;
            while (i >= 0 && i < h && !dark[i * w + x])
            {
                light++;
                i += dir;
            }
            while (i >= 0 && i < h && dark[i * w + x] && darkRun < module * 3)
            {
                darkRun++;
                i += dir;
            }
            return light >= module * 0.4 && light <= module * 1.9 && darkRun >= module * 0.4 && darkRun <= module * 2.2;
        }
    }

    private static void Add(List<Cluster> clusters, double x, double y, double module)
    {
        foreach (var c in clusters)
        {
            double d = Math.Max(Math.Abs(c.X - x), Math.Abs(c.Y - y));
            if (d < Math.Max(4, c.Module * 1.5))
            {
                c.SumX += x;
                c.SumY += y;
                c.SumModule += module;
                c.Count++;
                return;
            }
        }
        clusters.Add(new Cluster { SumX = x, SumY = y, SumModule = module, Count = 1 });
    }

    // ---------- Quad selection & orientation ----------

    private sealed record Quad((double X, double Y)[] Points, double Module);

    /// <summary>Chooses the four clusters that best form the finder rectangle (largest valid convex quad).</summary>
    private static Quad? ChooseQuad(List<Cluster> clusters)
    {
        var strong = clusters.Where(c => c.Count >= 2).OrderByDescending(c => c.Count).Take(12).ToList();
        if (strong.Count < 4)
            return null;

        Quad? best = null;
        double bestArea = 0;
        for (int a = 0; a < strong.Count - 3; a++)
        for (int b = a + 1; b < strong.Count - 2; b++)
        for (int c = b + 1; c < strong.Count - 1; c++)
        for (int d = c + 1; d < strong.Count; d++)
        {
            var set = new[] { strong[a], strong[b], strong[c], strong[d] };
            double minM = set.Min(s => s.Module), maxM = set.Max(s => s.Module);
            if (maxM > minM * 2.0)
                continue;

            var pts = OrderConvex(set.Select(s => (s.X, s.Y)).ToArray());
            if (pts is null)
                continue;

            double e0 = Dist(pts[0], pts[1]), e1 = Dist(pts[1], pts[2]);
            double e2 = Dist(pts[2], pts[3]), e3 = Dist(pts[3], pts[0]);
            double avgModule = set.Average(s => s.Module);
            if (Math.Min(e0, e2) * 1.8 < Math.Max(e0, e2) || Math.Min(e1, e3) * 1.8 < Math.Max(e1, e3))
                continue; // opposite edges wildly different — not a perspective view of a rectangle
            if (Math.Min(Math.Min(e0, e1), Math.Min(e2, e3)) < avgModule * 8)
                continue; // corners implausibly close together

            double area = ConvexArea(pts);
            if (area > bestArea)
            {
                bestArea = area;
                best = new Quad(pts, avgModule);
            }
        }
        return best;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Orders four points into a convex cycle around their centroid; null if not convex.</summary>
    private static (double X, double Y)[]? OrderConvex((double X, double Y)[] pts)
    {
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        var ordered = pts.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            var p0 = ordered[i];
            var p1 = ordered[(i + 1) % 4];
            var p2 = ordered[(i + 2) % 4];
            double cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            if (cross <= 0)
                return null; // collinear or non-convex
        }
        return ordered;
    }

    private static double ConvexArea((double X, double Y)[] p)
    {
        double area = 0;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2;
    }

    private sealed record OrientedQuad(
        (double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Br, (double X, double Y) Bl, double Module);

    /// <summary>
    /// Resolves which corner is which: the encoder draws a solid tick 7 modules along the top
    /// edge from the top-left finder center, so exactly one of the four cyclic assignments
    /// shows dark there and light at the mirrored position near the top-right finder.
    /// </summary>
    private static OrientedQuad? ResolveOrientation(Bitmap photo, bool[] dark, Quad quad)
    {
        OrientedQuad? resolved = null;
        for (int rot = 0; rot < 4; rot++)
        {
            var tl = quad.Points[rot];
            var tr = quad.Points[(rot + 1) % 4];
            var br = quad.Points[(rot + 2) % 4];
            var bl = quad.Points[(rot + 3) % 4];

            double topLen = Dist(tl, tr);
            if (topLen < 1)
                continue;
            double step = Layout.OrientationTickOffsetModules * quad.Module / topLen;
            var tick = (X: tl.X + (tr.X - tl.X) * step, Y: tl.Y + (tr.Y - tl.Y) * step);
            var anti = (X: tr.X + (tl.X - tr.X) * step, Y: tr.Y + (tl.Y - tr.Y) * step);

            int radius = Math.Max(2, (int)(quad.Module * 0.8));
            if (DarkFraction(photo, dark, tick, radius) > 0.6 && DarkFraction(photo, dark, anti, radius) < 0.3)
            {
                if (resolved is not null)
                    return null; // ambiguous — refuse rather than guess
                resolved = new OrientedQuad(tl, tr, br, bl, quad.Module);
            }
        }
        return resolved;
    }

    private static double DarkFraction(Bitmap photo, bool[] dark, (double X, double Y) center, int radius)
    {
        int x0 = Math.Clamp((int)center.X - radius, 0, photo.Width - 1);
        int x1 = Math.Clamp((int)center.X + radius, 0, photo.Width - 1);
        int y0 = Math.Clamp((int)center.Y - radius, 0, photo.Height - 1);
        int y1 = Math.Clamp((int)center.Y + radius, 0, photo.Height - 1);
        int total = 0, darkCount = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                total++;
                if (dark[y * photo.Width + x])
                    darkCount++;
            }
        }
        return total == 0 ? 0 : (double)darkCount / total;
    }

    // ---------- Rectification ----------

    private sealed record CanvasGeometry(Homography H, int Width, int Height, double Module);

    private static CanvasGeometry BuildGeometry(OrientedQuad q)
    {
        // Canvas geometry: finder centers at the corners of a (margin-inset) rectangle whose
        // size comes from the photographed edge lengths, so canvas scale ≈ photo scale and no
        // resolution is thrown away. Downstream tolerates unequal x/y scale by design.
        double wc = (Dist(q.Tl, q.Tr) + Dist(q.Bl, q.Br)) / 2;
        double hc = (Dist(q.Tl, q.Bl) + Dist(q.Tr, q.Br)) / 2;
        double margin = 8 * q.Module;

        double scale = Math.Min(1.0, MaxCanvasDimension / Math.Max(wc + 2 * margin, hc + 2 * margin));
        wc *= scale;
        hc *= scale;
        margin *= scale;

        int canvasW = (int)Math.Round(wc + 2 * margin);
        int canvasH = (int)Math.Round(hc + 2 * margin);

        Span<(double X, double Y)> canvasCorners =
        [
            (margin, margin), (margin + wc, margin), (margin + wc, margin + hc), (margin, margin + hc),
        ];
        Span<(double X, double Y)> photoCorners = [q.Tl, q.Tr, q.Br, q.Bl];
        return new CanvasGeometry(Homography.Solve(canvasCorners, photoCorners), canvasW, canvasH, q.Module * scale);
    }

    private static Bitmap WarpHomography(Bitmap photo, CanvasGeometry geometry)
    {
        var px = new Rgb24[geometry.Width * geometry.Height];
        for (int y = 0; y < geometry.Height; y++)
        {
            for (int x = 0; x < geometry.Width; x++)
            {
                var (sx, sy) = geometry.H.Apply(x + 0.5, y + 0.5);
                px[y * geometry.Width + x] = SampleBilinear(photo, sx - 0.5, sy - 0.5);
            }
        }
        return new Bitmap(px, geometry.Width, geometry.Height);
    }

    // ---------- Phase 2: frame-edge refinement + illumination normalization ----------

    private const int EdgeSamplesPerSide = 17;

    /// <summary>
    /// Locates the frame in the coarse canvas, traces its four edges in the original photo,
    /// and re-warps with a Coons-patch residual correction plus per-pixel black/white
    /// normalization. Returns null (caller keeps the phase-1 result) when the frame cannot be
    /// traced confidently.
    /// </summary>
    private static Bitmap? TryRefine(Bitmap photo, Bitmap coarse, CanvasGeometry geometry)
    {
        var box = FindCoarseFrameBox(coarse, geometry.Module);
        if (box is null)
            return null;
        var (bx0, by0, bx1, by1) = box.Value;

        var top = TraceSide(photo, geometry, i => (Lerp(bx0, bx1, (i + 0.5) / EdgeSamplesPerSide), by0), (0, -1));
        var bottom = TraceSide(photo, geometry, i => (Lerp(bx0, bx1, (i + 0.5) / EdgeSamplesPerSide), by1), (0, 1));
        var left = TraceSide(photo, geometry, i => (bx0, Lerp(by0, by1, (i + 0.5) / EdgeSamplesPerSide)), (-1, 0));
        var right = TraceSide(photo, geometry, i => (bx1, Lerp(by0, by1, (i + 0.5) / EdgeSamplesPerSide)), (1, 0));
        if (top is null || bottom is null || left is null || right is null)
            return null;

        var map = new RefinedMap(geometry.H, bx0, by0, bx1, by1, top, bottom, left, right);
        return WarpRefined(photo, map, geometry.Width, geometry.Height);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Approximate frame outer box in the coarse canvas, found by scanning for the strong dark
    /// bars from outside inward. Deliberately loose (bowed edges under lens distortion don't
    /// form clean rows) — every edge sample re-searches around it in the photo.
    /// </summary>
    private static (double X0, double Y0, double X1, double Y1)? FindCoarseFrameBox(Bitmap coarse, double module)
    {
        int w = coarse.Width, h = coarse.Height;
        // Finder centers sit at (8m, 8m)-style margins by canvas construction; the content
        // (and its frame) lies between the bands. Scan windows derived from module units.
        int? y0 = ScanRows(coarse, (int)(11 * module), (int)Math.Min(24 * module, h / 2.0), +1);
        int? y1 = ScanRows(coarse, h - 1 - (int)(11 * module), h - 1 - (int)Math.Min(24 * module, h / 2.0), -1);
        if (y0 is null || y1 is null || y1 - y0 < 8 * module)
            return null;
        int? x0 = ScanCols(coarse, (int)(1.5 * module), (int)Math.Min(9 * module, w / 2.0), +1, y0.Value, y1.Value);
        int? x1 = ScanCols(coarse, w - 1 - (int)(1.5 * module), w - 1 - (int)Math.Min(9 * module, w / 2.0), -1, y0.Value, y1.Value);
        if (x0 is null || x1 is null || x1 - x0 < 8 * module)
            return null;
        return (x0.Value, y0.Value, x1.Value, y1.Value);

        static int? ScanRows(Bitmap bmp, int from, int to, int step)
        {
            int lo = (int)(bmp.Width * 0.2), hi = (int)(bmp.Width * 0.8);
            for (int y = from; step > 0 ? y <= to : y >= to; y += step)
            {
                if (y < 0 || y >= bmp.Height)
                    continue;
                int dark = 0;
                for (int x = lo; x < hi; x++)
                    if (bmp.IsDark(x, y))
                        dark++;
                if (dark > (hi - lo) * 0.45)
                    return y;
            }
            return null;
        }

        static int? ScanCols(Bitmap bmp, int from, int to, int step, int y0, int y1)
        {
            int lo = y0 + (y1 - y0) / 4, hi = y1 - (y1 - y0) / 4;
            for (int x = from; step > 0 ? x <= to : x >= to; x += step)
            {
                if (x < 0 || x >= bmp.Width)
                    continue;
                int dark = 0;
                for (int y = lo; y < hi; y++)
                    if (bmp.IsDark(x, y))
                        dark++;
                if (dark > (hi - lo) * 0.45)
                    return x;
            }
            return null;
        }
    }

    /// <summary>Per-side traced data: normal-direction residuals plus local black/white colors.</summary>
    private sealed class SideTrace
    {
        public readonly double[] Dx = new double[EdgeSamplesPerSide];
        public readonly double[] Dy = new double[EdgeSamplesPerSide];
        public readonly double[][] Black = new double[EdgeSamplesPerSide][];
        public readonly double[][] White = new double[EdgeSamplesPerSide][];
        public readonly bool[] Valid = new bool[EdgeSamplesPerSide];
    }

    private static SideTrace? TraceSide(Bitmap photo, CanvasGeometry geometry,
        Func<int, (double X, double Y)> canvasPoint, (double X, double Y) outwardNormal)
    {
        var trace = new SideTrace();
        int valid = 0;
        for (int i = 0; i < EdgeSamplesPerSide; i++)
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
        if (valid < EdgeSamplesPerSide * 0.7)
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
            var c = SampleBilinear(photo, basePt.X + dir.X * t - 0.5, basePt.Y + dir.Y * t - 0.5);
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

    private static void FillInvalid(SideTrace trace)
    {
        for (int i = 0; i < EdgeSamplesPerSide; i++)
        {
            if (trace.Valid[i])
                continue;
            int prev = i - 1, next = i + 1;
            while (prev >= 0 && !trace.Valid[prev])
                prev--;
            while (next < EdgeSamplesPerSide && !trace.Valid[next])
                next++;
            int source = prev >= 0 ? prev : next;
            int other = next < EdgeSamplesPerSide ? next : source;
            double t = prev >= 0 && next < EdgeSamplesPerSide ? (double)(i - prev) / (next - prev) : 0;
            trace.Dx[i] = Lerp(trace.Dx[source], trace.Dx[other], t);
            trace.Dy[i] = Lerp(trace.Dy[source], trace.Dy[other], t);
            trace.Black[i] = LerpVec(trace.Black[source], trace.Black[other], t);
            trace.White[i] = LerpVec(trace.White[source], trace.White[other], t);
        }

        static double[] LerpVec(double[] a, double[] b, double t) =>
            [Lerp(a[0], b[0], t), Lerp(a[1], b[1], t), Lerp(a[2], b[2], t)];
    }

    /// <summary>
    /// Homography plus a Coons-patch field interpolated from the four traced sides: geometric
    /// residual (dx, dy) and local black/white colors, evaluated per canvas pixel.
    /// </summary>
    private sealed class RefinedMap(Homography h, double x0, double y0, double x1, double y1,
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
                var values = new double[EdgeSamplesPerSide];
                for (int i = 0; i < EdgeSamplesPerSide; i++)
                    values[i] = side[i][c];
                return values;
            }
        }

        /// <summary>Transfinite (Coons) interpolation from four boundary sample arrays.</summary>
        private static double Coons(double u, double v, double[] top, double[] bottom, double[] left, double[] right)
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

        private static double SideValue(double[] samples, double t)
        {
            double pos = t * EdgeSamplesPerSide - 0.5;
            int i0 = Math.Clamp((int)Math.Floor(pos), 0, EdgeSamplesPerSide - 1);
            int i1 = Math.Min(i0 + 1, EdgeSamplesPerSide - 1);
            return Lerp(samples[i0], samples[i1], Math.Clamp(pos - i0, 0, 1));
        }
    }

    private static Bitmap WarpRefined(Bitmap photo, RefinedMap map, int canvasW, int canvasH)
    {
        var px = new Rgb24[canvasW * canvasH];
        for (int y = 0; y < canvasH; y++)
        {
            for (int x = 0; x < canvasW; x++)
            {
                var (sx, sy) = map.Apply(x + 0.5, y + 0.5);
                var sample = SampleBilinear(photo, sx - 0.5, sy - 0.5);
                var (black, white) = map.Illumination(x + 0.5, y + 0.5);
                px[y * canvasW + x] = new Rgb24(
                    Normalize(sample.R, black[0], white[0]),
                    Normalize(sample.G, black[1], white[1]),
                    Normalize(sample.B, black[2], white[2]));
            }
        }
        return new Bitmap(px, canvasW, canvasH);

        // Linear per-channel remap against the locally interpolated black/white references —
        // this is what flattens vignette, glare gradients, and white-balance shifts.
        static byte Normalize(byte value, double black, double white)
        {
            double range = Math.Max(white - black, 24);
            return (byte)Math.Clamp((value - black) * 255.0 / range + 0.5, 0, 255);
        }
    }

    private static Rgb24 SampleBilinear(Bitmap photo, double x, double y)
    {
        if (x < -1 || y < -1 || x > photo.Width || y > photo.Height)
            return new Rgb24(255, 255, 255); // outside the photo: treat as quiet-zone white

        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        var p00 = At(photo, x0, y0);
        var p10 = At(photo, x0 + 1, y0);
        var p01 = At(photo, x0, y0 + 1);
        var p11 = At(photo, x0 + 1, y0 + 1);

        return new Rgb24(
            Mix(p00.R, p10.R, p01.R, p11.R, fx, fy),
            Mix(p00.G, p10.G, p01.G, p11.G, fx, fy),
            Mix(p00.B, p10.B, p01.B, p11.B, fx, fy));

        static Rgb24 At(Bitmap p, int x, int y) =>
            p.At(Math.Clamp(x, 0, p.Width - 1), Math.Clamp(y, 0, p.Height - 1));

        static byte Mix(byte a, byte b, byte c, byte d, double fx, double fy)
        {
            double top = a + (b - a) * fx;
            double bottom = c + (d - c) * fx;
            return (byte)Math.Clamp(top + (bottom - top) * fy + 0.5, 0, 255);
        }
    }
}
