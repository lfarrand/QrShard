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

        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, h), new InnerRect(0, 0, w, h), layout, palettes,
            new DecodeScratch(), out bool[]? suspects, out _);

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
    }

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
}
