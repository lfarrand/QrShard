namespace QrShard;

/// <summary>
/// Locates the shard's black locator frame in a capture: enumerates ring-shaped dark-component
/// candidates and validates each against the metadata strip until one decodes.
/// </summary>
internal sealed class FrameLocator(IInnerRectScanner innerRectScanner, IStripReader stripReader) : IFrameLocator
{
    /// <summary>The validated frame: its layout (from the metadata strip) and inner rectangle.</summary>
    public (Layout Layout, InnerRect Inner) Locate(Bitmap bmp, DecodeScratch scratch)
    {
        // Several dark rings can plausibly be the locator frame (e.g. a dark desktop border
        // around the capture also forms a ring); try candidates largest-first until the
        // metadata validates.
        var candidates = FindFrameCandidates(bmp, scratch.ClearedVisited(bmp.Width * bmp.Height));
        if (candidates.Count == 0)
            throw new ShardDecodeException("Could not locate the black frame. Crop the screenshot to the code (keep some white margin) and try again.");

        foreach (var frame in candidates.Take(8))
        {
            InnerRect inner;
            try
            {
                inner = innerRectScanner.FindInnerRect(bmp, frame);
            }
            catch (ShardDecodeException)
            {
                continue;
            }
            var layout = stripReader.ReadMetadata(bmp, inner);
            if (layout is not null)
                return (layout, inner);
        }
        throw new ShardDecodeException("Found a frame but the metadata strip is unreadable (CRC mismatch). The capture may be scaled too small or blurred.");
    }

    /// <summary>
    /// Finds locator-frame candidates: connected dark components whose bounding box is roughly
    /// square, ring-shaped (low fill density), and covers its own bounding-box edges.
    /// Returned largest-first; the caller validates each against the metadata strip.
    /// </summary>
    private static List<PixelRect> FindFrameCandidates(Bitmap bmp, bool[] visited)
    {
        int w = bmp.Width, h = bmp.Height;
        var stack = new Stack<int>();
        var candidates = new List<PixelRect>();

        for (int sy = 0; sy < h; sy += 2) // stride-2 seed scan; components are re-walked fully
        {
            for (int sx = 0; sx < w; sx += 2)
            {
                int seed = sy * w + sx;
                if (visited[seed] || !bmp.IsDark(sx, sy))
                    continue;

                int minX = sx, maxX = sx, minY = sy, maxY = sy;
                long pixels = 0;
                stack.Push(seed);
                visited[seed] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int x = p % w, y = p / w;
                    pixels++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    Visit(x - 1, y);
                    Visit(x + 1, y);
                    Visit(x, y - 1);
                    Visit(x, y + 1);
                }

                var box = new PixelRect(minX, minY, maxX + 1, maxY + 1);
                long area = (long)box.W * box.H;
                if (box.W < 100 || box.H < 100)
                    continue;
                double aspect = (double)box.W / box.H;
                double density = (double)pixels / area;
                if (aspect is < 0.3 or > 3.4 || density is < 0.005 or > 0.6)
                    continue;
                if (EdgeCoverage(bmp, box) < 0.85)
                    continue;
                candidates.Add(box);

                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        return;
                    int np = ny * w + nx;
                    if (!visited[np] && bmp.IsDark(nx, ny))
                    {
                        visited[np] = true;
                        stack.Push(np);
                    }
                }
            }
        }
        return [.. candidates.OrderByDescending(c => (long)c.W * c.H)];
    }

    /// <summary>
    /// Fraction of the bounding-box perimeter that is dark (a frame ring covers ~all of it).
    /// Checked with a small inward tolerance band: resampled captures (camera rectification,
    /// rescaled screenshots) leave the ring's edges wavy by a pixel or two, and the exact
    /// bounding-box row only touches a wavy edge where it peaks.
    /// </summary>
    private static double EdgeCoverage(Bitmap bmp, PixelRect box)
    {
        const int tolerance = 2;
        int innerTop = Math.Min(box.Y0 + tolerance, box.Y1 - 1);
        int innerBottom = Math.Max(box.Y1 - 1 - tolerance, box.Y0);
        int innerLeft = Math.Min(box.X0 + tolerance, box.X1 - 1);
        int innerRight = Math.Max(box.X1 - 1 - tolerance, box.X0);

        long dark = 0, total = 0;
        for (int x = box.X0; x < box.X1; x++)
        {
            total += 2;
            if (AnyDarkY(bmp, x, box.Y0, innerTop)) dark++;
            if (AnyDarkY(bmp, x, innerBottom, box.Y1 - 1)) dark++;
        }
        for (int y = box.Y0; y < box.Y1; y++)
        {
            total += 2;
            if (AnyDarkX(bmp, y, box.X0, innerLeft)) dark++;
            if (AnyDarkX(bmp, y, innerRight, box.X1 - 1)) dark++;
        }
        return (double)dark / total;

        static bool AnyDarkY(Bitmap bmp, int x, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++)
                if (bmp.IsDark(x, y))
                    return true;
            return false;
        }

        static bool AnyDarkX(Bitmap bmp, int y, int x0, int x1)
        {
            for (int x = x0; x <= x1; x++)
                if (bmp.IsDark(x, y))
                    return true;
            return false;
        }
    }
}
