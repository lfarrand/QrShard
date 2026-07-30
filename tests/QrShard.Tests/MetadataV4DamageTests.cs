using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The point of metadata version 4, at the level that matters: a real capture, decoded end to end,
/// with damage that version 2 could not survive.
///
/// The strip is read before the data grid's Reed-Solomon is consulted, so under version 2 a single
/// flipped module lost an image whose payload was intact and fully protected. The top/bottom
/// duplication does not cover it, because both copies sit at the same x as each other — one narrow
/// vertical mark reaches the same modules in both.
/// </summary>
public class MetadataV4DamageTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    /// <summary>Blacks out <paramref name="modules"/> modules from <paramref name="firstModule"/>
    /// in BOTH metadata strips — the shared-x geometry the duplication cannot defend.</summary>
    private static void DamageBothStrips(Image<Rgb24> img, Layout layout, int firstModule, int modules)
    {
        double moduleW = (double)(layout.InnerW - 2 * layout.Gutter) / Layout.MetaModuleCount;
        int x0 = (int)(Layout.Border + layout.Gutter + firstModule * moduleW);
        int x1 = (int)Math.Ceiling(Layout.Border + layout.Gutter + (firstModule + modules) * moduleW);
        int topBand = Layout.Border + layout.Gutter;
        int bottomBand = Layout.Border + layout.InnerH - layout.Gutter - layout.MetaH;

        img.ProcessPixelRows(acc =>
        {
            foreach (int bandY in (int[])[topBand, bottomBand])
                for (int y = bandY; y < bandY + layout.MetaH && y < img.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
                        row[x] = new Rgb24(0, 0, 0);
                }
        });
    }

    private static (byte[] Content, string Shard, Layout Layout) Encode(TempDir tmp)
    {
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);
        return (content, result.Files[0], layout);
    }

    [Fact]
    public void OneCorruptedSymbolInBothStrips_StillDecodes()
    {
        using var tmp = new TempDir();
        var (content, shard, layout) = Encode(tmp);

        string damaged = tmp.File("damaged.png");
        using (var img = Image.Load<Rgb24>(shard))
        {
            DamageBothStrips(img, layout, firstModule: 24, modules: 8); // exactly one RS symbol
            img.SaveAsPng(damaged);
        }

        // Under version 2 this image was unreadable: the strip failed its CRC in both copies and
        // the decode gave up before touching the grid.
        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([damaged], restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void TwoCorruptedSymbolsInBothStrips_StillDecodes()
    {
        using var tmp = new TempDir();
        var (content, shard, layout) = Encode(tmp);

        string damaged = tmp.File("damaged.png");
        using (var img = Image.Load<Rgb24>(shard))
        {
            DamageBothStrips(img, layout, firstModule: 32, modules: 16); // the full correction budget
            img.SaveAsPng(damaged);
        }

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([damaged], restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void BeyondTheCorrectionBudget_FailsCleanly()
    {
        // Past two symbols the strip must be REFUSED, not guessed at. A miscorrected strip would
        // hand the decoder a plausible but wrong geometry, which is worse than a clear failure —
        // the CRC underneath the RS exists to catch exactly that.
        using var tmp = new TempDir();
        var (_, shard, layout) = Encode(tmp);

        string damaged = tmp.File("damaged.png");
        using (var img = Image.Load<Rgb24>(shard))
        {
            DamageBothStrips(img, layout, firstModule: 16, modules: 40); // five symbols
            img.SaveAsPng(damaged);
        }

        var ex = Record.Exception(() => new ShardDecoder().DecodeFolder([damaged], tmp.File("out.bin"), _ => { }));
        Assert.IsType<ShardDecodeException>(ex);
    }
}
