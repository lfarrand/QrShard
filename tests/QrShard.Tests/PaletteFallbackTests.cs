using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Both palette strips sit at the SAME x as each other — a block's centre depends only on its
/// index — so one vertical mark can poison the same entry in both copies. The decoder then picked
/// the "better" of two equally-damaged strips and classified against it. The theoretical palette
/// was already being built here, but only as a yardstick for choosing between the measured pair;
/// nothing ever fell back to it, and GridSampler consumed the result unguarded.
///
/// It is always available and correct by construction — SPEC section 3 derives the palette from
/// bitsPerCell alone — so a decoder can regenerate it without reading anything from the image.
/// </summary>
public class PaletteFallbackTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (Bitmap Bmp, InnerRect Inner, Layout Layout) Locate(string path)
    {
        var scratch = new DecodeScratch();
        using var img = Image.Load<Rgb24>(path);
        var px = new Rgb24[img.Width * img.Height];
        img.CopyPixelDataTo(px);
        var bmp = new Bitmap(px, img.Width, img.Height);
        var (layout, inner) = new FrameLocator(new InnerRectScanner(), new StripReader()).Locate(bmp, scratch);
        return (bmp, inner, layout);
    }

    private static string Encode(TempDir tmp, out byte[] content)
    {
        content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        return new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast).Files[0];
    }

    [Fact]
    public void CleanCapture_StillUsesTheMeasuredPalette()
    {
        // The guard must not cost the measured strip its job. If this fires on an undamaged
        // capture, every colour transform the strip exists to track is being thrown away — a far
        // worse outcome than the damage case it is meant to cover.
        using var tmp = new TempDir();
        string shard = Encode(tmp, out _);
        var (bmp, inner, layout) = Locate(shard);

        var palettes = new StripReader().ReadPalette(bmp, inner, layout);
        var theoretical = new Palette().Build(layout.BitsPerCell);

        // A rendered shard's measured strip is essentially exact, so this asserts the SOURCE
        // rather than the values: Top and Bottom are only populated on the measured path.
        Assert.NotSame(theoretical, palettes.Best);
        Assert.NotSame(palettes.Top, palettes.Bottom);
    }

    [Fact]
    public void BothPaletteStripsPoisoned_FallsBackToTheTheoreticalPalette()
    {
        using var tmp = new TempDir();
        string shard = Encode(tmp, out _);
        var (_, _, layout) = Locate(shard);

        // Paint a black column through the SAME palette block in both copies — the geometry that
        // makes the top/bottom duplication no defence. Both strips lose the same entry, so
        // choosing the better of the two changes nothing.
        string damaged = tmp.File("damaged.png");
        using (var img = Image.Load<Rgb24>(shard))
        {
            int blocks = 1 << layout.BitsPerCell;
            int stripW = layout.InnerW - 2 * layout.Gutter;
            int blockW = stripW / blocks;
            // Border offsets the inner area; block 3 of 16 is comfortably inside the strip.
            int x = Layout.Border + layout.Gutter + 3 * blockW + blockW / 2;
            int x0 = x - blockW / 3, x1 = x + blockW / 3;
            // Two short marks, one per palette band, NOT a full-height column: a column through
            // the quiet zone and frame ring would stop the frame being located at all, which is a
            // different failure. The bands are ~2000 px apart in a real capture and the marks need
            // no relationship beyond sharing an x — which the shared block geometry supplies.
            int topBand = Layout.Border + layout.Gutter + layout.MetaH;
            int bottomBand = Layout.Border + layout.InnerH - layout.Gutter - 2 * layout.MetaH;
            img.ProcessPixelRows(acc =>
            {
                foreach (int bandY in (int[])[topBand, bottomBand])
                    for (int y = bandY; y < bandY + layout.MetaH && y < img.Height; y++)
                    {
                        var row = acc.GetRowSpan(y);
                        for (int px2 = Math.Max(0, x0); px2 < Math.Min(img.Width, x1); px2++)
                            row[px2] = new Rgb24(0, 0, 0);
                    }
            });
            img.SaveAsPng(damaged);
        }

        var (bmp2, inner2, layout2) = Locate(damaged);
        var palettes = new StripReader().ReadPalette(bmp2, inner2, layout2);
        var theoretical = new Palette().Build(layout2.BitsPerCell);

        // Fallback substitutes the theoretical palette for all three slots and turns interpolation
        // off — neither measured strip is trustworthy enough to interpolate between.
        Assert.Equal(theoretical, palettes.Best);
        Assert.Equal(theoretical, palettes.Top);
        Assert.Equal(theoretical, palettes.Bottom);
        Assert.False(palettes.Interpolate);
    }

    [Fact]
    public void SeparableGainShiftedStrip_BeatsCloserCollapsedStrip()
    {
        Rgb24[] theoretical = new Palette().Build(bitsPerCell: 4);
        var healthy = theoretical.Select(p => new Rgb24(
            (byte)Math.Round(p.R * 0.45),
            (byte)Math.Round(p.G * 0.45),
            (byte)Math.Round(p.B * 0.45))).ToArray();
        var collapsed = (Rgb24[])theoretical.Clone();
        collapsed[5] = collapsed[4];

        // The damaged copy differs in only one entry and is therefore much closer by squared
        // colour distance. Separability must take precedence or strong but valid gain loses.
        Assert.Same(healthy, StripReader.SelectBestMeasured(collapsed, healthy, theoretical));
        Assert.Same(healthy, StripReader.SelectBestMeasured(healthy, collapsed, theoretical));
    }
}
