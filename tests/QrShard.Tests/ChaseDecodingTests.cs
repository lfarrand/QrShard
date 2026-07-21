using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Chase decoding: spending the classifier's runner-up values on codewords past both capacities.</summary>
public class ChaseDecodingTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    [Fact]
    public void ChaseSubsets_RescueCodeword_BeyondErrorsAndErasures()
    {
        // One codeword, engineered so every earlier stage provably fails:
        //  - 11 corrupted symbols (> parity/2 = 8): errors-only fails;
        //  - only 4 of them flagged: 2*7 + 4 = 18 > 16: erasures fail;
        //  - Chase flips the 4 flagged symbols to their (correct) second choices,
        //    leaving 7 unknown errors <= 8: decodes.
        const int parity = 16, cwCount = 1;
        var fec = new Fec();
        int dataLen = Fec.DataLength(parity);
        var stream = TestData.Random(dataLen, seed: 9);
        var buffer = fec.Protect(stream, parity, cwCount);
        var clean = (byte[])buffer.Clone();

        int[] flagged = [5, 30, 60, 90];
        int[] unflagged = [10, 40, 70, 100, 130, 160, 190];
        var suspects = new bool[buffer.Length];
        var second = (byte[])clean.Clone(); // second choice = the true value at flagged spots
        foreach (int p in flagged)
        {
            buffer[p] ^= 0x2D;
            suspects[p] = true;
        }
        foreach (int p in unflagged)
            buffer[p] ^= 0x4B;

        var dest = new byte[dataLen];
        Assert.False(fec.TryRecoverInto(buffer, parity, cwCount, dest, out _));
        Assert.False(fec.TryRecoverInto(buffer, parity, cwCount, dest, out _, null, suspects));
        Assert.True(fec.TryRecoverInto(buffer, parity, cwCount, dest, out int corrected, null, suspects, second));
        Assert.Equal(stream, dest);
        Assert.True(corrected >= unflagged.Length);
    }

    [Fact]
    public void BlendedDamage_AllFlipTrial_RecoversSystematicMisclassification()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        var probe = new ShardDecoder().Diagnose(result.Files[0]);
        var layout = probe.Layout!;
        var palette = new Palette().Build(layout.BitsPerCell);
        var paletteMath = new Palette();

        // Blend a band of cells 55% toward a DIFFERENT palette color: the classifier's best
        // choice flips to the wrong color while the true one becomes the runner-up — the
        // systematic-blur failure mode. Sized at ~20 suspect bytes/codeword: beyond the
        // erasure cap (14), so only the Chase all-flip trial can bring it home.
        int targetBytesPerCw = 20;
        int bandRows = Math.Max(1, (int)Math.Ceiling(2.0 * targetBytesPerCw * layout.CodewordCount / layout.GridW));
        int dataX = Layout.Border + layout.DataLeft;
        int dataY = layout.ContentTop + Layout.Border + layout.DataTop;
        int gy0 = layout.GridH / 2 - bandRows / 2;

        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            for (int gy = gy0; gy < gy0 + bandRows; gy++)
            {
                for (int gx = 0; gx < layout.GridW; gx++)
                {
                    int x0 = dataX + gx * layout.CellPx, y0 = dataY + gy * layout.CellPx;
                    var current = img[x0 + 1, y0 + 1];
                    int v = paletteMath.Nearest(palette, current.R, current.G, current.B);
                    var other = palette[v ^ 1];
                    var blended = new Rgb24(
                        (byte)(current.R * 0.45 + other.R * 0.55),
                        (byte)(current.G * 0.45 + other.G * 0.55),
                        (byte)(current.B * 0.45 + other.B * 0.55));
                    for (int y = y0; y < y0 + layout.CellPx; y++)
                        for (int x = x0; x < x0 + layout.CellPx; x++)
                            img[x, y] = blended;
                }
            }
            img.SaveAsPng(result.Files[0]);
        }

        // A/B at the FEC layer: same cells, with and without the second-choice stream.
        var scratch = new DecodeScratch();
        Assert.True(new FastPngReader().TryRead(result.Files[0], scratch, out Bitmap bmp));
        var (foundLayout, inner) = new FrameLocator(new InnerRectScanner(), new StripReader()).Locate(bmp, scratch);
        var palettes = new StripReader().ReadPalette(bmp, inner, foundLayout);
        byte[] cells = new GridSampler().ReadDataGrid(bmp, inner, foundLayout, palettes, scratch,
            out bool[]? suspects, out byte[]? secondChoice);
        Assert.NotNull(suspects);
        Assert.NotNull(secondChoice);

        var fec = new Fec();
        var dest = new byte[foundLayout.CodewordCount * Fec.DataLength(foundLayout.EccParity)];
        Assert.False(fec.TryRecoverInto(cells, foundLayout.EccParity, foundLayout.CodewordCount, dest, out _,
            null, suspects));
        Assert.True(fec.TryRecoverInto(cells, foundLayout.EccParity, foundLayout.CodewordCount, dest, out _,
            null, suspects, secondChoice));

        // And the full pipeline round-trips.
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([result.Files[0]], output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
