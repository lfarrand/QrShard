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
        var candidates = FindFrameCandidates(bmp, scratch.ClearedVisited(bmp.Width * bmp.Height), out _);
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
    /// Per-image decode pixel cap used by <c>FastPngReader</c> and <c>ShardDecoder</c>. The
    /// flood-fill stack is sized from this budget so a 500 MP all-black PNG cannot grow a
    /// ~2 GB <see cref="Stack{T}"/> on top of RGB + visited.
    /// </summary>
    internal const int DecodePixelBudget = 500_000_000;

    /// <summary>
    /// Live DFS frontier cap for an image of this size. Derived from
    /// <see cref="DecodePixelBudget"/> (at most ~15.6 MB of ints) and the admitted raster
    /// (a ring's frontier scales with perimeter × thickness, not with a solid fill).
    /// </summary>
    internal static int FloodFillStackLimit(int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels < 1)
            return 1;
        long fromBudget = DecodePixelBudget / 128;
        long fromPerimeter = 64L * ((long)width + height);
        long fromPixels = pixels / 8;
        return (int)Math.Clamp(Math.Min(fromBudget, Math.Max(fromPerimeter, fromPixels)), 1, int.MaxValue);
    }

    /// <summary>
    /// Finds locator-frame candidates: connected dark components whose bounding box is roughly
    /// square, ring-shaped (low fill density), and covers its own bounding-box edges.
    /// Returned largest-first; the caller validates each against the metadata strip.
    /// </summary>
    internal static List<PixelRect> FindFrameCandidates(Bitmap bmp, bool[] visited) =>
        FindFrameCandidates(bmp, visited, out _);

    internal static List<PixelRect> FindFrameCandidates(Bitmap bmp, bool[] visited, out long pixelsWalked)
    {
        int w = bmp.Width, h = bmp.Height;
        int stackLimit = FloodFillStackLimit(w, h);
        long imageArea = (long)w * h;
        var stack = new Stack<int>();
        var candidates = new List<PixelRect>();
        pixelsWalked = 0;

        for (int sy = 0; sy < h; sy += 2) // stride-2 seed scan; components are re-walked fully
        {
            for (int sx = 0; sx < w; sx += 2)
            {
                int seed = sy * w + sx;
                if (visited[seed] || !bmp.IsDark(sx, sy))
                    continue;

                int minX = sx, maxX = sx, minY = sy, maxY = sy;
                long pixels = 0;
                bool aborted = false;
                stack.Push(seed);
                visited[seed] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int x = p % w, y = p / w;
                    pixels++;
                    pixelsWalked++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (CannotPassLater(pixels, minX, maxX, minY, maxY, w, h, imageArea)
                        || stack.Count >= stackLimit)
                    {
                        aborted = true;
                        stack.Clear();
                        break;
                    }
                    Visit(x - 1, y);
                    Visit(x + 1, y);
                    Visit(x, y - 1);
                    Visit(x, y + 1);
                    if (aborted)
                    {
                        stack.Clear();
                        break;
                    }
                }

                if (aborted)
                {
                    // The remainder of this ocean must not be re-seeded (that would walk a
                    // 500 MP fill in bounded chunks). Expand each touched row through the
                    // dark run that already contains a visited pixel; a white quiet zone
                    // stops the run, so an isolated locator ring stays findable.
                    MarkDarkRunsOnTouchedRows(bmp, visited, minX, maxX, minY, maxY, w);
                    continue;
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
                    if (aborted || nx < 0 || ny < 0 || nx >= w || ny >= h)
                        return;
                    int np = ny * w + nx;
                    if (!visited[np] && bmp.IsDark(nx, ny))
                    {
                        if (stack.Count >= stackLimit)
                        {
                            aborted = true;
                            return;
                        }
                        visited[np] = true;
                        stack.Push(np);
                    }
                }
            }
        }
        return [.. candidates.OrderByDescending(c => (long)c.W * c.H)];
    }

    /// <summary>
    /// True when this walk can no longer satisfy the post-DFS density (&gt; 0.6 of the
    /// image, the largest possible frame), aspect ([0.3, 3.4]), or minimum-area (100×100)
    /// predicates, even if the bounding box grows to the image edges.
    /// </summary>
    private static bool CannotPassLater(long pixels, int minX, int maxX, int minY, int maxY,
        int w, int h, long imageArea)
    {
        int bw = maxX - minX + 1;
        int bh = maxY - minY + 1;
        // density = pixels/area; area cannot exceed the image. 0.6 = 3/5.
        if (pixels * 5 > imageArea * 3)
            return true;
        // aspect = bw/bh. The box only grows: too-wide if bw/h > 3.4 (17/5),
        // too-tall if w/bh < 0.3 (3/10).
        if ((long)bw * 5 > (long)h * 17)
            return true;
        if ((long)bh * 3 > (long)w * 10)
            return true;
        // Height/width already spans the image and is still below the 100 px floor.
        if (bh < 100 && minY == 0 && maxY == h - 1)
            return true;
        if (bw < 100 && minX == 0 && maxX == w - 1)
            return true;
        return false;
    }

    /// <summary>
    /// After an aborted walk, mark the rest of each horizontally contiguous dark run that
    /// already contains a visited pixel, so the seed scan does not restart the same fill.
    /// </summary>
    private static void MarkDarkRunsOnTouchedRows(Bitmap bmp, bool[] visited,
        int minX, int maxX, int minY, int maxY, int w)
    {
        int x0 = Math.Max(minX, 0);
        int x1 = Math.Min(maxX, w - 1);
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * w;
            int x = 0;
            while (x < w)
            {
                if (!bmp.IsDark(x, y))
                {
                    x++;
                    continue;
                }
                int start = x;
                while (x < w && bmp.IsDark(x, y))
                    x++;
                if (start > x1 || x - 1 < x0)
                    continue;
                bool hit = false;
                for (int i = start; i < x; i++)
                {
                    if (visited[row + i])
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                    continue;
                for (int i = start; i < x; i++)
                    visited[row + i] = true;
            }
        }
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
