using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// 10-bit neighbours sit 17 apart. A fixed floor of 200 treats squared distance 64 as certain,
/// so a cell encoded as red 0 and captured as red 9 is classified as red 17 and never flagged.
/// </summary>
public class TenBitConfidenceTests
{
    [Fact]
    public void ConfidenceFloor_FollowsMeasuredPaletteSpacing()
    {
        Assert.Equal(49, GridSampler.ConfidenceFloorSquared(17 * 17));
        Assert.Equal(196, GridSampler.ConfidenceFloorSquared(36 * 36));

        var palette = new Palette().Build(10);
        var set = new PaletteSet(palette, palette, palette, Interpolate: false);
        Assert.Equal(49, GridSampler.ConfidenceFloorFor(set));
    }

    [Fact]
    public void NoisyTenBitCapture_FlagsTheNearTie_AndNotAnExactOtherColour()
    {
        var palette = new Palette().Build(10);
        Assert.Equal(new Rgb24(0, 0, 0), palette[0]);
        Assert.Equal(new Rgb24(17, 0, 0), palette[64]);

        // 10-bit cells share bytes with their neighbours. Cells 0, 3, and 6 do not.
        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 7,
            GridH = 1,
            MetaH = 1,
            InnerW = 2 * 1 + 7,
            InnerH = 6 * 1 + 1,
            EccParity = 16,
            FinderModule = 0,
        };
        int w = layout.InnerW, h = layout.InnerH;
        var px = new Rgb24[w * h];
        int row = 3; // floor(DataTop + 0.5) with DataTop = 3
        px[row * w + 1] = new Rgb24(4, 0, 0);  // closer to red 0 than to red 17; under the floor
        px[row * w + 4] = new Rgb24(9, 0, 0);  // classified as red 17 at distance 64
        px[row * w + 7] = new Rgb24(17, 0, 0); // exact hit on the other colour
        var palettes = new PaletteSet(palette, palette, palette, Interpolate: false);

        var nearTies = new bool[layout.GridW];
        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout, palettes,
            new DecodeScratch(), out bool[]? suspects, out _, nearTieCells: nearTies);

        var bits = new BitStream();
        Assert.Equal(0, bits.ReadCell(stream, 0, 10));
        Assert.Equal(64, bits.ReadCell(stream, 30, 10));
        Assert.Equal(64, bits.ReadCell(stream, 60, 10));
        Assert.NotNull(suspects);
        Assert.False(suspects[0]);
        Assert.False(suspects[1]);
        Assert.True(suspects[3]);
        Assert.True(suspects[4]);
        Assert.False(suspects[7]);
        Assert.False(suspects[8]);
        Assert.False(nearTies[0]);
        Assert.True(nearTies[3]);
        Assert.False(nearTies[6]);
    }

    [Fact]
    public void QualityHeatmap_UsesTheTenBitFloor()
    {
        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 2,
            GridH = 1,
            MetaH = 6,
            InnerW = 14,
            InnerH = 37,
            EccParity = 0,
            FinderModule = 0,
        };
        using var tmp = new TempDir();
        string path = tmp.File("quality.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 64], path, confidentDist: 49);

        using var image = Image.Load<Rgb24>(path);
        Rgb24 exact = image[0, 0];
        Rgb24 nearTie = image[6, 0];
        Assert.NotEqual(exact, nearTie);

        string oldFloor = tmp.File("old-floor.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 64], oldFloor, confidentDist: 200);
        using var previous = Image.Load<Rgb24>(oldFloor);
        Assert.Equal(previous[0, 0], previous[6, 0]);

        // The unflagged gradient leaves distance 64 almost green. A flagged near-tie is a warning,
        // and an exact tie is a different red. An unflagged weak cell stays on the gradient.
        string warned = tmp.File("warned.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 64], warned, confidentDist: 49, nearTieCells: [false, true]);
        using var warning = Image.Load<Rgb24>(warned);
        Assert.Equal(exact, warning[0, 0]);
        Assert.Equal(HeatmapRenderer.NearTieWarning, warning[6, 0]);
        Assert.NotEqual(nearTie, warning[6, 0]);

        string tied = tmp.File("exact-tie.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 0], tied, confidentDist: 49, ambiguousCells: [false, true]);
        using var tie = Image.Load<Rgb24>(tied);
        Assert.NotEqual(warning[6, 0], tie[6, 0]);
        Assert.NotEqual(exact, tie[6, 0]);

        string weak = tmp.File("weak.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 2000], weak, confidentDist: 49);
        using var weakImage = Image.Load<Rgb24>(weak);
        Assert.NotEqual(HeatmapRenderer.NearTieWarning, weakImage[6, 0]);
        Assert.NotEqual(exact, weakImage[6, 0]);
    }

    [Fact]
    public void InterpolatedMidpoint_FlagsANearTieTheEndpointFloorWouldSkip()
    {
        var top = new Palette().Build(10);
        var bottom = (Rgb24[])top.Clone();
        (bottom[0], bottom[64]) = (bottom[64], bottom[0]);
        Assert.Equal(new Rgb24(17, 0, 0), bottom[0]);
        // Each strip is still a full 10-bit palette, so the endpoint floor stays 49.
        Assert.Equal(49, GridSampler.ConfidenceFloorSquared(Palette.ClosestSquared(top)));
        Assert.Equal(49, GridSampler.ConfidenceFloorSquared(Palette.ClosestSquared(bottom)));

        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 1,
            GridH = 1,
            MetaH = 1,
            InnerW = 3,
            InnerH = 7,
            EccParity = 16,
            FinderModule = 0,
        };
        var palettes = new PaletteSet(top, top, bottom, Interpolate: true);
        Assert.Equal(0.5, GridSampler.InterpolatedRowT(layout, 0), precision: 6);
        Assert.Equal(0, GridSampler.ConfidenceFloorFor(palettes, layout));

        int w = layout.InnerW, h = layout.InnerH;
        var px = new Rgb24[w * h];
        px[3 * w + 1] = new Rgb24(10, 0, 0);
        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout, palettes,
            new DecodeScratch(), out bool[]? suspects, out _);

        Assert.Equal(0, new BitStream().ReadCell(stream, 0, 10));
        Assert.NotNull(suspects);
        Assert.True(suspects[0]);
        Assert.True(suspects[1]);

        // The two swapped reds meet at one colour. A sample of that colour is distance 0 from
        // both indices, so the winner is unknowable and the other index is the Chase alternative.
        Rgb24 coincident = LerpChannel(top[0], bottom[0]);
        Assert.Equal(coincident, LerpChannel(top[64], bottom[64]));
        px[3 * w + 1] = coincident;
        byte[] tied = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout, palettes,
            new DecodeScratch(), out bool[]? tieSuspects, out byte[]? second);
        Assert.Equal(0, new BitStream().ReadCell(tied, 0, 10));
        Assert.NotNull(tieSuspects);
        Assert.True(tieSuspects[0]);
        Assert.True(tieSuspects[1]);
        Assert.Equal(64, new BitStream().ReadCell(second!, 0, 10));

        // The same row still has unique colours. An exact hit on one of those stays confident.
        Assert.Equal(top[1], bottom[1]);
        px[3 * w + 1] = top[1];
        byte[] unique = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout, palettes,
            new DecodeScratch(), out bool[]? uniqueSuspects, out byte[]? uniqueSecond);
        Assert.Equal(1, new BitStream().ReadCell(unique, 0, 10));
        Assert.NotNull(uniqueSuspects);
        Assert.False(uniqueSuspects[0]);
        Assert.False(uniqueSuspects[1]);
        Assert.Equal(1, new BitStream().ReadCell(uniqueSecond!, 0, 10));

        // No ECC: erasure flags are not allocated, and both cells store margin 0. The tie marker
        // is what keeps the quality heatmap from painting the coincident colour as confident.
        var noEcc = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 1,
            GridH = 1,
            MetaH = 1,
            InnerW = 3,
            InnerH = 7,
            EccParity = 0,
            FinderModule = 0,
        };
        var tieMargins = new int[1];
        var tieAmbiguous = new bool[1];
        px[3 * w + 1] = coincident;
        new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), noEcc, palettes,
            new DecodeScratch(), out bool[]? tieNoEcc, out _, tieMargins, tieAmbiguous);
        Assert.Null(tieNoEcc);
        Assert.Equal(0, tieMargins[0]);
        Assert.True(tieAmbiguous[0]);

        var uniqueMargins = new int[1];
        var uniqueAmbiguous = new bool[1];
        px[3 * w + 1] = top[1];
        new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), noEcc, palettes,
            new DecodeScratch(), out bool[]? uniqueNoEcc, out _, uniqueMargins, uniqueAmbiguous);
        Assert.Null(uniqueNoEcc);
        Assert.Equal(0, uniqueMargins[0]);
        Assert.False(uniqueAmbiguous[0]);

        var diag = new DecodeDiagnostics { WantDetail = true, Layout = noEcc };
        diag.CellMargins = tieMargins;
        diag.AmbiguousCells = tieAmbiguous;
        diag.RowConfidentDist = [0];
        using var tmp = new TempDir();
        string tiedHeat = tmp.File("tied.png");
        string uniqueHeat = tmp.File("unique.png");
        string unmarkedHeat = tmp.File("unmarked.png");
        var heatmap = new HeatmapRenderer();
        heatmap.RenderQuality(diag.Layout, diag.CellMargins, tiedHeat, diag.QualityConfidentDist, diag.RowConfidentDist, diag.AmbiguousCells);
        heatmap.RenderQuality(noEcc, uniqueMargins, uniqueHeat, rowFloors: [0], ambiguousCells: uniqueAmbiguous);
        heatmap.RenderQuality(noEcc, tieMargins, unmarkedHeat, rowFloors: [0]);
        using var tiedImage = Image.Load<Rgb24>(tiedHeat);
        using var uniqueImage = Image.Load<Rgb24>(uniqueHeat);
        using var unmarkedImage = Image.Load<Rgb24>(unmarkedHeat);
        Assert.Equal(uniqueImage[0, 0], unmarkedImage[0, 0]);
        Assert.NotEqual(uniqueImage[0, 0], tiedImage[0, 0]);
    }

    private static Rgb24 LerpChannel(Rgb24 a, Rgb24 b) => new(
        (byte)(a.R + (b.R - a.R) * 0.5 + 0.5),
        (byte)(a.G + (b.G - a.G) * 0.5 + 0.5),
        (byte)(a.B + (b.B - a.B) * 0.5 + 0.5));

    [Fact]
    public void QualityHeatmap_ClampsASparsePaletteFloorAtTheAbsoluteThreshold()
    {
        var blackAndWhite = new Palette().Build(1);
        long floor = GridSampler.ConfidenceFloorFor(new PaletteSet(blackAndWhite, blackAndWhite, blackAndWhite, false));
        Assert.True(floor > GridSampler.AbsoluteSuspectDist);

        var layout = new Layout
        {
            BitsPerCell = 1,
            CellPx = 1,
            GridW = 2,
            GridH = 1,
            MetaH = 6,
            InnerW = 14,
            InnerH = 37,
            EccParity = 0,
            FinderModule = 0,
        };
        using var tmp = new TempDir();
        string trusted = tmp.File("trusted.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 1000], trusted, floor);
        using var image = Image.Load<Rgb24>(trusted);
        Assert.Equal(image[0, 0], image[6, 0]);

        string past = tmp.File("past.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 5000], past, GridSampler.AbsoluteSuspectDist);
        using var far = Image.Load<Rgb24>(past);
        Assert.NotEqual(far[0, 0], far[6, 0]);
    }

    [Fact]
    public void UniformLookup_MatchesTheScan_IncludingTiesAndABrokenPalette()
    {
        var palette = new Palette().Build(10);
        // Red 9 is nearer red 17. Green 18 is an exact tie between 0 and 36; the lowest index wins.
        AssertUniformSymbol(palette, new Rgb24(9, 0, 0), 64);
        AssertUniformSymbol(palette, new Rgb24(0, 18, 0), 0);
        AssertUniformSymbol(palette, new Rgb24(17, 0, 0), 64);

        var broken = (Rgb24[])new Palette().Build(8).Clone();
        broken[10] = broken[2];
        int tied = new Palette().Nearest(broken, broken[2].R, broken[2].G, broken[2].B);
        Assert.Equal(2, tied);
        AssertUniformSymbol(broken, broken[2], tied);
    }

    [Fact]
    public void QualityHeatmap_UsesEachInterpolatedRowsFloor()
    {
        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 1,
            GridH = 2,
            MetaH = 6,
            InnerW = 14,
            InnerH = 38,
            EccParity = 0,
            FinderModule = 0,
        };
        long[] floors = [49, 9];
        using var tmp = new TempDir();
        string path = tmp.File("rows.png");
        new HeatmapRenderer().RenderQuality(layout, [36, 36], path, confidentDist: 9, rowFloors: floors);
        using var image = Image.Load<Rgb24>(path);
        string greenPath = tmp.File("green.png");
        new HeatmapRenderer().RenderQuality(layout, [0, 0], greenPath, confidentDist: 49);
        using var green = Image.Load<Rgb24>(greenPath);
        Assert.Equal(green[0, 0], image[0, 0]);
        Assert.NotEqual(green[0, 0], image[0, 6]);

        string collapsed = tmp.File("min.png");
        new HeatmapRenderer().RenderQuality(layout, [36, 36], collapsed, confidentDist: 9);
        using var min = Image.Load<Rgb24>(collapsed);
        Assert.Equal(min[0, 0], min[0, 6]);
    }

    [Fact]
    public void InterpolatedSampling_RecordsAFloorPerRow()
    {
        var top = new Palette().Build(10);
        var bottom = top.Select(p => new Rgb24(
            (byte)(p.R * 0.5 + 0.5),
            (byte)(p.G * 0.5 + 0.5),
            (byte)(p.B * 0.5 + 0.5))).ToArray();
        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 1,
            GridH = 2,
            MetaH = 1,
            InnerW = 3,
            InnerH = 8,
            EccParity = 0,
            FinderModule = 0,
        };
        var scratch = new DecodeScratch();
        new GridSampler().ReadDataGrid(
            new Bitmap(new Rgb24[layout.InnerW * layout.InnerH], layout.InnerW, layout.InnerH),
            new InnerRect(0, 0, layout.InnerW, layout.InnerH), layout,
            new PaletteSet(top, top, bottom, Interpolate: true), scratch,
            out _, out _, new int[2]);

        long[] floors = scratch.CopyRowFloors();
        Assert.Equal(2, floors.Length);
        Assert.True(floors[0] > floors[1]);
        long between = (floors[0] + floors[1]) / 2;
        Assert.True(between > floors[1] && between <= floors[0]);
    }

    [Fact]
    public void Diagnose_UniformCameraRetry_DropsTheInterpolatedRowFloors()
    {
        var layout = new Layout
        {
            BitsPerCell = 8,
            CellPx = 1,
            GridW = 1,
            GridH = 1,
            MetaH = 1,
            InnerW = 3,
            InnerH = 7,
            EccParity = 0,
            FinderModule = 0,
        };
        var sampler = new RetrySampler();
        var decoder = new ShardDecoder(
            AppSettings.BuiltIn, new SameBitmapRectifier(), new FixedFrameLocator(layout),
            new SwitchingStripReader(), sampler, new ShardAssembler(), new Fec(), new Crc(),
            new FastPngReader(), new PhotoFusion(), new Interleaver2());
        using var tmp = new TempDir();
        string path = tmp.File("capture.png");
        using (var image = new Image<Rgb24>(1, 1))
            image.SaveAsPng(path);

        DecodeDiagnostics diag = decoder.Diagnose(path);

        Assert.Equal(2, sampler.Calls);
        Assert.Null(diag.RowConfidentDist);
        Assert.Equal(RetrySampler.UniformFloor, diag.QualityConfidentDist);
        Assert.NotNull(diag.Error);
        Assert.NotNull(diag.Layout);
        Assert.NotNull(diag.CellMargins);

        string fromDiag = tmp.File("from-diag.png");
        string uniform = tmp.File("uniform.png");
        string stale = tmp.File("stale.png");
        var heatmap = new HeatmapRenderer();
        heatmap.RenderQuality(diag.Layout, diag.CellMargins, fromDiag,
            diag.QualityConfidentDist, diag.RowConfidentDist);
        heatmap.RenderQuality(diag.Layout, diag.CellMargins, uniform,
            diag.QualityConfidentDist, rowFloors: null);
        heatmap.RenderQuality(diag.Layout, diag.CellMargins, stale,
            diag.QualityConfidentDist, rowFloors: [RetrySampler.InterpolatedFloor]);
        using var painted = Image.Load<Rgb24>(fromDiag);
        using var expected = Image.Load<Rgb24>(uniform);
        using var leaked = Image.Load<Rgb24>(stale);
        Assert.Equal(expected[0, 0], painted[0, 0]);
        Assert.NotEqual(leaked[0, 0], painted[0, 0]);
    }

    private static void AssertUniformSymbol(Rgb24[] palette, Rgb24 sample, int expected)
    {
        int bits = palette.Length == 1 << 10 ? 10 : 8;
        var layout = new Layout
        {
            BitsPerCell = bits,
            CellPx = 1,
            GridW = 1,
            GridH = 1,
            MetaH = 1,
            InnerW = 3,
            InnerH = 7,
            EccParity = 0,
            FinderModule = 0,
        };
        int w = layout.InnerW, h = layout.InnerH;
        var px = new Rgb24[w * h];
        px[3 * w + 1] = sample;
        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout,
            new PaletteSet(palette, palette, palette, Interpolate: false),
            new DecodeScratch(), out _, out _);
        Assert.Equal(expected, new BitStream().ReadCell(stream, 0, bits));
        Assert.Equal(expected, new Palette().Nearest(palette, sample.R, sample.G, sample.B));
    }

    /// <summary>First sample interpolates and fails the header; the camera retry is uniform.</summary>
    private sealed class SwitchingStripReader : IStripReader
    {
        private int calls;

        public Layout? ReadMetadata(Bitmap bmp, InnerRect inner) => null;

        public PaletteSet ReadPalette(Bitmap bmp, InnerRect inner, Layout layout)
        {
            calls++;
            Rgb24[] colors = [new Rgb24(0, 0, 0)];
            return new PaletteSet(colors, colors, colors, Interpolate: calls == 1);
        }
    }

    private sealed class RetrySampler : IGridSampler
    {
        internal const long InterpolatedFloor = 111;
        internal const long UniformFloor = 49;
        internal const int Margin = 80;
        internal int Calls { get; private set; }

        public byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes,
            DecodeScratch scratch, out bool[]? suspectBytes, out byte[]? secondChoiceBytes, int[]? cellMargins = null,
            bool[]? ambiguousCells = null, bool[]? nearTieCells = null)
        {
            Calls++;
            suspectBytes = null;
            secondChoiceBytes = null;
            if (cellMargins is { Length: > 0 })
                cellMargins[0] = Margin;
            if (Calls == 1)
                scratch.RowFloors(layout.GridH)[0] = InterpolatedFloor;
            else
                scratch.UniformConfidenceFloor = UniformFloor;
            return [0xFF];
        }
    }

    private sealed class FixedFrameLocator(Layout layout) : IFrameLocator
    {
        public (Layout Layout, InnerRect Inner) Locate(Bitmap bmp, DecodeScratch scratch) =>
            (layout, new InnerRect(0, 0, layout.InnerW, layout.InnerH));
    }

    private sealed class SameBitmapRectifier : ICameraRectifier
    {
        public Bitmap? TryRectify(Bitmap photo) => photo;

        public CameraPose? DetectPose(Bitmap photo) => null;

        public Bitmap RectifyWithPose(Bitmap photo, CameraPose pose) => photo;
    }
}
