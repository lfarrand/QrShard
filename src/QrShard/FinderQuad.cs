namespace QrShard;

internal sealed record FinderQuad((double X, double Y)[] Points, double Module);

internal sealed record OrientedQuad(
    (double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Br, (double X, double Y) Bl, double Module);

/// <summary>
/// Turns finder-pattern clusters into an oriented corner quad: picks the four clusters that
/// best form the finder rectangle, then resolves which corner is which via the encoder's
/// orientation tick.
/// </summary>
internal sealed class QuadSelector(CameraMath math) : IQuadSelector
{
    public QuadSelector() : this(new CameraMath())
    {
    }

    /// <summary>Chooses the four clusters that best form the finder rectangle (largest valid convex quad).</summary>
    public FinderQuad? ChooseQuad(List<FinderCluster> clusters)
    {
        var strong = clusters.Where(c => c.Count >= 2).OrderByDescending(c => c.Count).Take(12).ToList();
        if (strong.Count < 4)
            return null;

        FinderQuad? best = null;
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

            double e0 = math.Dist(pts[0], pts[1]), e1 = math.Dist(pts[1], pts[2]);
            double e2 = math.Dist(pts[2], pts[3]), e3 = math.Dist(pts[3], pts[0]);
            double avgModule = set.Average(s => s.Module);
            if (Math.Min(e0, e2) * 1.8 < Math.Max(e0, e2) || Math.Min(e1, e3) * 1.8 < Math.Max(e1, e3))
                continue; // opposite edges wildly different — not a perspective view of a rectangle
            if (Math.Min(Math.Min(e0, e1), Math.Min(e2, e3)) < avgModule * 8)
                continue; // corners implausibly close together

            double area = ConvexArea(pts);
            if (area > bestArea)
            {
                bestArea = area;
                best = new FinderQuad(pts, avgModule);
            }
        }
        return best;
    }

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

    /// <summary>
    /// Resolves which corner is which: the encoder draws a solid tick 7 modules along the top
    /// edge from the top-left finder center, so exactly one of the four cyclic assignments
    /// shows dark there and light at the mirrored position near the top-right finder.
    /// </summary>
    public OrientedQuad? ResolveOrientation(Bitmap photo, bool[] dark, FinderQuad quad)
    {
        OrientedQuad? resolved = null;
        for (int rot = 0; rot < 4; rot++)
        {
            var tl = quad.Points[rot];
            var tr = quad.Points[(rot + 1) % 4];
            var br = quad.Points[(rot + 2) % 4];
            var bl = quad.Points[(rot + 3) % 4];

            double topLen = math.Dist(tl, tr);
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
}
