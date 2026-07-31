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
