namespace QrShard;

/// <summary>
/// Approximate frame outer box in the coarse canvas, found by scanning for the strong dark
/// bars from outside inward. Deliberately loose (bowed edges under lens distortion don't
/// form clean rows) — every edge sample re-searches around it in the photo.
/// </summary>
internal static class CoarseFrameScanner
{
    public static (double X0, double Y0, double X1, double Y1)? FindFrameBox(Bitmap coarse, double module)
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
    }

    private static int? ScanRows(Bitmap bmp, int from, int to, int step)
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

    private static int? ScanCols(Bitmap bmp, int from, int to, int step, int y0, int y1)
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
