using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// Three findings that shared a shape: a guard, a message and a fallback that all existed, were
/// all documented as working, and none of which could actually fire.
/// </summary>
public class ThreeFinderAndOversizeTests
{
    private sealed class FixedClusters(List<FinderCluster> clusters) : IFinderDetector
    {
        public List<FinderCluster> FindCandidates(Bitmap photo, bool[] dark) => clusters;
    }

    private sealed class OversizeBinarizer : IAdaptiveBinarizer
    {
        public const string Message = "Image is 9000x9000; too large for camera-capture binarization "
                                    + "(limit 80,000,000 pixels). Crop closer to the shard, or capture at a lower resolution.";
        public bool[] Threshold(Bitmap photo) => throw new ShardDecodeException(Message);
    }

    /// <summary>A cluster whose centroid is (x, y): FinderCluster divides the sums by Count.</summary>
    private static FinderCluster At(double x, double y, double module)
    {
        const int votes = 5;
        return new FinderCluster { SumX = x * votes, SumY = y * votes, SumModule = module * votes, Count = votes };
    }

    // ---------- ThreeFinderQuad ----------

    [Fact]
    public void AThreeFinderQuadReconstructsTheOccludedCorner()
    {
        // The L: top-left is the right-angle vertex, bottom-right is missing.
        var quad = new QuadSelector().ChooseQuad([At(100, 100, 10), At(900, 100, 10), At(100, 900, 10)]);

        Assert.NotNull(quad);
        Assert.Contains(quad.Points, p => Math.Abs(p.X - 900) < 1e-6 && Math.Abs(p.Y - 900) < 1e-6);
    }

    [Theory]
    [InlineData(1.00, true)]   // frontal: parallelogram completion is exact
    [InlineData(1.015, true)]  // just inside — about two modules of reconstruction error
    [InlineData(1.05, false)]  // ~5 modules
    [InlineData(1.30, false)]  // ~34 modules; the old bound of 1.6 accepted this
    public void TheModuleAgreementBoundActuallyBitesNow(double ratio, bool accepted)
    {
        // The three post-construction checks this replaced were provably dead: the construction
        // makes an exact parallelogram, so the opposite-edge test reduced to `e * 1.8 < e`, and the
        // min-edge test duplicated a check the vertex loop had already made. Only the module ratio
        // can constrain this reconstruction, and at 1.6 it was admitting 60+ modules of error.
        var quad = new QuadSelector().ChooseQuad(
            [At(100, 100, 10), At(900, 100, 10 * ratio), At(100, 900, 10)]);

        Assert.Equal(accepted, quad is not null);
    }

    [Fact]
    public void AnOppositeEdgeCheckCouldNeverHaveFiredOnThisConstruction()
    {
        // Stated as a property rather than as history: whatever three points go in, the quad that
        // comes out is a parallelogram, so its opposite edges are equal to within rounding. Any
        // future "opposite edges disagree" guard placed here would be dead on arrival.
        var rnd = new Random(4242);
        for (int i = 0; i < 500; i++)
        {
            var quad = new QuadSelector().ChooseQuad([
                At(100 + rnd.Next(200), 100 + rnd.Next(200), 10),
                At(800 + rnd.Next(200), 120 + rnd.Next(200), 10),
                At(120 + rnd.Next(200), 800 + rnd.Next(200), 10)]);
            if (quad is null) continue;

            var p = quad.Points;
            double e0 = Dist(p[0], p[1]), e1 = Dist(p[1], p[2]), e2 = Dist(p[2], p[3]), e3 = Dist(p[3], p[0]);
            Assert.True(Math.Abs(e0 - e2) < 1e-6, $"opposite edges differ by {Math.Abs(e0 - e2)}");
            Assert.True(Math.Abs(e1 - e3) < 1e-6, $"opposite edges differ by {Math.Abs(e1 - e3)}");
        }
        static double Dist((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// <summary>Records whether the quad selector was consulted at all.</summary>
    private sealed class SpySelector : IQuadSelector
    {
        private readonly QuadSelector _real = new();
        public int ChooseQuadCalls { get; private set; }

        public FinderQuad? ChooseQuad(List<FinderCluster> clusters)
        {
            ChooseQuadCalls++;
            return _real.ChooseQuad(clusters);
        }

        public OrientedQuad? ResolveOrientation(Bitmap photo, bool[] dark, FinderQuad quad) =>
            _real.ResolveOrientation(photo, dark, quad);
    }

    [Fact]
    public void DetectPoseConsultsTheQuadSelectorWhenOnlyThreeFindersAreVisible()
    {
        // The pre-check demanded FOUR raw clusters, so a capture yielding precisely the three
        // visible finders was refused before the fallback written to rescue it was ever consulted.
        // Asserting "no exception" would not catch that — both versions return null quietly. What
        // distinguishes them is whether ChooseQuad is reached at all.
        var spy = new SpySelector();
        var rectifier = new CameraRectifier(
            new AdaptiveBinarizer(),
            new FixedClusters([At(150, 150, 12), At(950, 150, 12), At(150, 950, 12)]),
            spy, new CoarseFrameScanner(), new FrameEdgeTracer(), new CameraMath());

        rectifier.DetectPose(new Bitmap(new Rgb24[1100 * 1100], 1100, 1100));

        Assert.Equal(1, spy.ChooseQuadCalls);
    }

    // ---------- the oversize message ----------

    [Fact]
    public void TheCameraPathsRefusalReachesTheUser()
    {
        // AdaptiveBinarizer composes actionable advice for an over-80-megapixel photo, and all
        // three TryRectify call sites caught it, discarded it, and rethrew the axis-aligned error
        // instead. A real 9000x9000 capture was told "Could not locate the black frame".
        var decoder = new ShardDecoder(
            AppSettings.Current,
            new CameraRectifier(new OversizeBinarizer(), new FinderDetector(), new QuadSelector(),
                new CoarseFrameScanner(), new FrameEdgeTracer(), new CameraMath()),
            new FrameLocator(new InnerRectScanner(), new StripReader()),
            new StripReader(), new GridSampler(), new ShardAssembler(),
            new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        using var tmp = new TempDir();
        string photo = tmp.File("photo.png");
        using (var blank = new Image<Rgb24>(600, 600, new Rgb24(200, 200, 200)))
            blank.SaveAsPng(photo);

        var thrown = Record.Exception(() => decoder.DecodeImage(photo, new DecodeScratch()));

        var typed = Assert.IsType<ShardDecodeException>(thrown);
        Assert.Contains("Crop closer to the shard", typed.Message);
    }
}
