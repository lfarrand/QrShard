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
}
