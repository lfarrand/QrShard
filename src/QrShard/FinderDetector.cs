namespace QrShard;

/// <summary>An agreeing group of finder-pattern scanline hits; centroid = pattern center.</summary>
internal sealed class FinderCluster
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
internal static class FinderDetector
{
    public static List<FinderCluster> FindCandidates(Bitmap photo, bool[] dark)
    {
        int w = photo.Width, h = photo.Height;
        double minModule = Math.Max(3.0, Math.Min(w, h) / 400.0);
        var clusters = new List<FinderCluster>();

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

    private static void Add(List<FinderCluster> clusters, double x, double y, double module)
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
        clusters.Add(new FinderCluster { SumX = x, SumY = y, SumModule = module, Count = 1 });
    }
}
