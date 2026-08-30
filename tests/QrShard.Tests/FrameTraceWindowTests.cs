using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// TraceEdge shrank its search WINDOW when the profile needed more than 512 samples, pinning the
/// half-window at (512 - 1) * 0.5 / 2 = 127.75 px for every module above 42.6. Since the final
/// check is `thickness &lt;= search`, a frame thicker than 127.75 px in the photo could never be
/// traced — TraceSide returned null, TryRefine returned null, and phase-2 refinement switched off
/// silently on exactly the close-up captures it helps most.
///
/// The comment above that clamp named the right principle — "the cost is coarser resolution on a
/// very large module rather than no result at all" — and then did the opposite. The fix is to
/// coarsen the step, so the window always spans +-3 modules.
/// </summary>
public class FrameTraceWindowTests
{
    private const int W = 800, H = 800, FrameTop = 300;

    /// <summary>White quiet zone, a black frame band of the given thickness, white interior.</summary>
    private static Bitmap FrameStrip(int thickness)
    {
        var px = new Rgb24[W * H];
        for (int y = 0; y < H; y++)
        {
            var c = y >= FrameTop && y < FrameTop + thickness
                ? new Rgb24(12, 12, 12)
                : new Rgb24(240, 240, 240);
            for (int x = 0; x < W; x++)
                px[y * W + x] = c;
        }
        return new Bitmap(px, W, H);
    }

    private static int TracedSamples(int thickness, double module)
    {
        var photo = FrameStrip(thickness);
        var identity = Homography.Solve([(0, 0), (W, 0), (W, H), (0, H)], [(0, 0), (W, 0), (W, H), (0, H)]);
        var geometry = new CanvasGeometry(identity, W, H, module);
        double innerY = FrameTop + thickness;
        var trace = new FrameEdgeTracer().TraceSide(photo, geometry, i => (100.0 + i * 35, innerY), (0, -1));
        return trace?.Valid.Count(v => v) ?? 0;
    }

    [Theory]
    [InlineData(128)] // the old cliff, to the pixel
    [InlineData(130)]
    [InlineData(240)]
    public void AFrameThickerThanTheOldWindowIsStillTraced(int thickness)
    {
        // module 110 gives search = 330 px, so all three are comfortably inside the window that
        // the physics allows. Before the fix every one of them returned null.
        Assert.Equal(SideTrace.SamplesPerSide, TracedSamples(thickness, module: 110));
    }

    [Fact]
    public void TheTraceableThicknessScalesWithTheModuleRatherThanBeingAbsolute()
    {
        // The property that catches this whole class of defect. A larger module must be able to
        // trace a thicker frame; if two very different modules share a ceiling, that ceiling is an
        // implementation artefact rather than a physical bound. Before the fix, module 45 and
        // module 110 both failed at exactly 128 px.
        const int thick = 240;

        Assert.Equal(0, TracedSamples(thick, module: 20));   // search = 60  — legitimately too narrow
        Assert.Equal(0, TracedSamples(thick, module: 45));   // search = 135 — legitimately too narrow
        Assert.Equal(SideTrace.SamplesPerSide, TracedSamples(thick, module: 110)); // search = 330 — fits
    }

    [Fact]
    public void ASmallModuleStillRejectsAFrameItCannotPhysicallyContain()
    {
        // The module-relative bound is real and must survive the fix: search is 3 modules, so a
        // frame wider than that is genuinely untraceable and must stay rejected rather than being
        // waved through by a wider window.
        Assert.Equal(0, TracedSamples(thickness: 120, module: 20));
        Assert.Equal(SideTrace.SamplesPerSide, TracedSamples(thickness: 50, module: 20));
    }
}

/// <summary>
/// Flood-fill abort for TASK-011: a solid dark field must not be walked to completion, and a
/// real locator ring must still be admitted.
/// </summary>
public class FrameLocatorFloodFillTests
{
    private static Bitmap Solid(int w, int h, Rgb24 color)
    {
        var px = new Rgb24[w * h];
        Array.Fill(px, color);
        return new Bitmap(px, w, h);
    }

    /// <summary>White field, black ring of <paramref name="thickness"/> inset by <paramref name="quiet"/>.</summary>
    private static Bitmap LocatorRing(int w, int h, int thickness, int quiet)
    {
        var px = new Rgb24[w * h];
        Array.Fill(px, new Rgb24(240, 240, 240));
        var black = new Rgb24(12, 12, 12);
        int x0 = quiet, y0 = quiet, x1 = w - quiet, y1 = h - quiet;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                bool inner = x >= x0 + thickness && x < x1 - thickness
                          && y >= y0 + thickness && y < y1 - thickness;
                if (!inner)
                    px[y * w + x] = black;
            }
        }
        return new Bitmap(px, w, h);
    }

    private static List<PixelRect> Candidates(Bitmap bmp, out long walked)
    {
        var visited = new bool[bmp.Width * bmp.Height];
        return FrameLocator.FindFrameCandidates(bmp, visited, out walked);
    }

    [Fact]
    public void ALocatorRingIsStillAdmitted()
    {
        var bmp = LocatorRing(400, 400, thickness: 16, quiet: 12);
        var found = Candidates(bmp, out long walked);

        Assert.NotEmpty(found);
        Assert.Equal(12, found[0].X0);
        Assert.Equal(12, found[0].Y0);
        Assert.Equal(388, found[0].X1);
        Assert.Equal(388, found[0].Y1);
        Assert.True(walked < 400L * 400, "a 16 px ring is a few tens of thousands of pixels, not the field");
    }

    [Fact]
    public void AnAllBlackFieldIsRejectedWithoutWalkingTheWholeComponent()
    {
        const int n = 400;
        var bmp = Solid(n, n, new Rgb24(0, 0, 0));
        var found = Candidates(bmp, out long walked);

        Assert.Empty(found);
        long limit = FrameLocator.FloodFillStackLimit(n, n);
        Assert.True(walked <= limit + n, // one abort plus a leftover spine at most
            $"walked {walked} pixels of a {n * n} field; stack limit is {limit}");
        Assert.True(walked < n * n / 2, "must not complete a solid fill just to apply density > 0.6 afterwards");
    }

    [Fact]
    public void AShortDarkBarCannotPassTheAreaFloor()
    {
        // 40 px tall: cannot reach the 100 px area floor even at the image height it already spans.
        const int w = 400, h = 40;
        var bmp = Solid(w, h, new Rgb24(0, 0, 0));
        var found = Candidates(bmp, out long walked);

        Assert.Empty(found);
        Assert.True(walked < w * h, "unrecoverable height must abort before the last pixel");
    }

    [Fact]
    public void ARingIsolatedFromADarkFieldByQuietZoneIsStillFound()
    {
        // Dark wallpaper on the left, white gap, locator ring on the right. Aborting the
        // wallpaper fill must stop at the quiet zone rather than painting over the ring.
        const int w = 600, h = 400;
        var px = new Rgb24[w * h];
        Array.Fill(px, new Rgb24(240, 240, 240));
        var black = new Rgb24(12, 12, 12);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < 180; x++)
                px[y * w + x] = black;
        int quiet = 200, thickness = 16, x1 = 580, y0 = 12, y1 = 388;
        for (int y = y0; y < y1; y++)
        {
            for (int x = quiet; x < x1; x++)
            {
                bool inner = x >= quiet + thickness && x < x1 - thickness
                          && y >= y0 + thickness && y < y1 - thickness;
                if (!inner)
                    px[y * w + x] = black;
            }
        }

        var found = Candidates(new Bitmap(px, w, h), out _);
        Assert.Contains(found, r => r.X0 == quiet && r.Y0 == y0 && r.X1 == x1 && r.Y1 == y1);
    }

    [Fact]
    public void StackLimitStaysFarBelowTheDecodePixelBudget()
    {
        int limit = FrameLocator.FloodFillStackLimit(25_000, 20_000);
        Assert.True(limit <= FrameLocator.DecodePixelBudget / 128);
        Assert.True(limit < 5_000_000, "a 500 MP fill must not be allowed a 2 GB int stack");
    }

    [Fact]
    public void ARenderedShardStillLocates()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(20_000));
        string shard = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 }).Files[0];

        var scratch = new DecodeScratch();
        Assert.True(new FastPngReader().TryRead(shard, scratch, out Bitmap bmp));
        var (layout, inner) = new FrameLocator(new InnerRectScanner(), new StripReader()).Locate(bmp, scratch);
        Assert.True(layout.GridW > 0);
        Assert.True(inner.W > 100);
    }
}
