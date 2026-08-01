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
            // Partial occlusion (a finger/glare/edge-clip over one corner) leaves only three
            // finders. Reconstruct the fourth by parallelogram completion so a common handheld
            // capture that today fails outright still decodes — the payload CRC gates any bad
            // reconstruction, so a wrong quad simply produces a capture that does not decode.
            return strong.Count == 3 ? ThreeFinderQuad(strong) : null;

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

    /// <summary>
    /// Reconstructs a finder quad from exactly three clusters (one corner occluded). Identifies
    /// the right-angle corner of the L they form and synthesizes the opposite corner by
    /// parallelogram completion (fourth = a + c − vertex).
    ///
    /// The module-agreement bound is the ONLY thing standing between this and a badly wrong quad,
    /// and it used to be nowhere near tight enough. Parallelogram completion is exact for an
    /// affine view and degrades linearly with foreshortening, which the module ratio measures
    /// directly. Measured on a perspective projection of a square of finder centres:
    ///
    ///     tilt          0     5    10    15    20    30    40 degrees
    ///     moduleRatio 1.000 1.032 1.065 1.098 1.131 1.198 1.262
    ///     error         0.0   3.7   7.4  10.9  14.4  20.8  26.5 modules
    ///
    /// so error ≈ 113·(ratio − 1) modules. The old bound of 1.6 admitted more than 60 modules of
    /// error, and the doc claimed the corner ANGLE was the strict guard — it is not: |cos| only
    /// reaches 0.149 at 40° tilt against a 0.30 threshold, so it accepts everything this can get
    /// wrong. 1.02 holds the error near two modules, which is about the frame thickness the
    /// scanner downstream has to find.
    ///
    /// The comment here also used to claim validation "with the same edge and module checks as the
    /// four-finder path". Those checks could not fire. fourth = a + c − vertex makes an exact
    /// parallelogram, so opposite edges are identically equal and Min(e0,e2)·1.8 &lt; Max(e0,e2)
    /// reduces to e·1.8 &lt; e; and every edge equals one of the two vectors already tested against
    /// avgModule·8 above, so the min-edge check was equally dead. All three were removed rather
    /// than repaired, because the module bound is what actually constrains this construction.
    ///
    /// If the tilt limit ever needs raising, the answer is not a looser ratio but a better
    /// reconstruction: module is proportional to 1/depth, which is exactly the homogeneous weight,
    /// so P₄ ≅ P_a/m_a + P_c/m_c − P_v/m_v is EXACT at any tilt (measured: 0.00 modules out to
    /// 50°). It trades tilt sensitivity for module-noise sensitivity — about 210·noise modules,
    /// tilt-independent, so roughly 4 modules at 2% module error — and wins beyond a ratio of
    /// about 1.04. Not adopted here because this path is near-unreachable today (see DetectPose),
    /// so the safe, exact-when-it-runs construction is the better trade.
    /// </summary>
    private FinderQuad? ThreeFinderQuad(List<FinderCluster> three)
    {
        double minM = three.Min(s => s.Module), maxM = three.Max(s => s.Module);
        // Bounds the reconstruction error to about two modules; see the note above for the curve
        // this comes from. Far tighter than the four-finder path's 2.0 because that path measures
        // all four corners while this one INFERS one, and the inference is only as good as the
        // view is affine.
        if (maxM > minM * MaxThreeFinderModuleRatio)
            return null;
        double avgModule = three.Average(s => s.Module);
        var p = three.Select(s => (X: s.X, Y: s.Y)).ToArray();

        // The right-angle vertex is the point whose two edges to the others are most perpendicular.
        int vertex = -1;
        double bestCos = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3, k = (i + 2) % 3;
            double v1x = p[j].X - p[i].X, v1y = p[j].Y - p[i].Y;
            double v2x = p[k].X - p[i].X, v2y = p[k].Y - p[i].Y;
            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y), len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (len1 < avgModule * 8 || len2 < avgModule * 8)
                return null; // corners implausibly close
            double cos = Math.Abs((v1x * v2x + v1y * v2y) / (len1 * len2));
            if (cos < bestCos)
            {
                bestCos = cos;
                vertex = i;
            }
        }
        if (vertex < 0 || bestCos > 0.30) // within ~17° of a right angle (allows moderate perspective)
            return null;

        int a = (vertex + 1) % 3, c = (vertex + 2) % 3;
        var fourth = (X: p[a].X + p[c].X - p[vertex].X, Y: p[a].Y + p[c].Y - p[vertex].Y);
        var pts = OrderConvex([p[vertex], p[a], fourth, p[c]]);
        return pts is null ? null : new FinderQuad(pts, avgModule);
    }

    /// <summary>
    /// Largest module disagreement a three-finder reconstruction may show. The parallelogram
    /// completion it uses is exact only for an affine view, and this ratio is what measures the
    /// departure from one.
    /// </summary>
    private const double MaxThreeFinderModuleRatio = 1.02;

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

            // FinderDetector measures the module from HORIZONTAL scanlines, so a shard rotated
            // in-plane by phi reports it inflated by 1/cos(phi): a row crossing a band of width m
            // whose normal is turned away from x traverses m/cos(phi) pixels. The error folds with
            // 90-degree symmetry (at 90 the scan simply measures the other axis), so it peaks at
            // 45 degrees, where the module comes back 41% too large.
            //
            // That is not a degradation, it is a cliff. The tick sits SEVEN modules along the top
            // edge, so a 19% overestimate displaces the probe by 1.33 modules against a disc of
            // radius 0.8 — the disc lands entirely off the tick, DarkFraction falls under 0.6,
            // ResolveOrientation returns null, and the capture is refused outright. Measured end
            // to end on simulated captures, decoding failed for every rotation from 33 to 55
            // degrees and succeeded either side of it:
            //
            //     rot   25   30   33   45   55   58   90
            //           ok   ok  FAIL FAIL FAIL   ok   ok
            //
            // Photographing a screen at 45 degrees is an entirely ordinary thing to do, and the
            // error it produced named nothing that would lead a user to straighten up.
            double phi = Math.Atan2(tr.Y - tl.Y, tr.X - tl.X);
            double folded = phi - Math.PI / 2 * Math.Round(phi / (Math.PI / 2));
            double module = quad.Module * Math.Cos(folded);

            // The tick is a fixed number of modules along the shard's top edge, which is a fact in
            // the SHARD's plane. Walking the same FRACTION along the photo edge is only correct
            // for an affine view -- a projective map does not preserve ratios along a line -- and
            // the error compounds with the module one above. Map the canvas point through the same
            // homography the rectifier will build instead.
            double wc = (topLen + math.Dist(bl, br)) / 2;
            double hc = (math.Dist(tl, bl) + math.Dist(tr, br)) / 2;
            double offset = Layout.OrientationTickOffsetModules * module;
            if (offset * 2 >= wc)
                continue; // the tick and its mirror would cross — not a plausible labelling
            (double X, double Y) tick, anti;
            try
            {
                var h = Homography.Solve([(0, 0), (wc, 0), (wc, hc), (0, hc)], [tl, tr, br, bl]);
                tick = h.Apply(offset, 0);
                anti = h.Apply(wc - offset, 0);
            }
            catch (ShardDecodeException)
            {
                continue; // degenerate labelling — another rotation may still resolve
            }

            int radius = Math.Max(2, (int)(module * 0.8));
            if (DarkFraction(photo, dark, tick, radius) > 0.6 && DarkFraction(photo, dark, anti, radius) < 0.3)
            {
                if (resolved is not null)
                    return null; // ambiguous — refuse rather than guess
                // The corrected module travels on, so BuildGeometry's 8-module margin and the
                // canvas scale are sized from the real one rather than the inflated one.
                resolved = new OrientedQuad(tl, tr, br, bl, module);
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
