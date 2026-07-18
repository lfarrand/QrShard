using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Camera-capture front-end (phase 1): finds the four QR-style finder patterns that
/// camera-profile shards carry in their top/bottom bands, resolves orientation via the tick
/// mark next to the top-left finder, solves the 4-point homography, and resamples the photo
/// into an axis-aligned canvas that the ordinary decode pipeline can consume.
///
/// Handles rotation (any angle, including 90/180/270), perspective from off-axis shots, and
/// the mild blur/JPEG artifacts of a real photo. Lens-distortion correction via an alignment
/// lattice is phase 2.
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

        return Rectify(photo, oriented);
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

    private static Bitmap Rectify(Bitmap photo, OrientedQuad q)
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
        var h = Homography.Solve(canvasCorners, photoCorners);

        var px = new Rgb24[canvasW * canvasH];
        for (int y = 0; y < canvasH; y++)
        {
            for (int x = 0; x < canvasW; x++)
            {
                var (sx, sy) = h.Apply(x + 0.5, y + 0.5);
                px[y * canvasW + x] = SampleBilinear(photo, sx - 0.5, sy - 0.5);
            }
        }
        return new Bitmap(px, canvasW, canvasH);
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
