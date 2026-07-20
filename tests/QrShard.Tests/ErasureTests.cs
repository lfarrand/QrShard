using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Errors-and-erasures Reed-Solomon decoding and the ambiguous-cell flags that feed it.</summary>
public class ErasureTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static byte[] MakeCodeword(int dataLen, int nsym, int seed)
    {
        var rs = new ReedSolomon();
        var cw = new byte[dataLen + nsym];
        new Random(seed).NextBytes(cw.AsSpan(0, dataLen));
        rs.Encode(cw.AsSpan(0, dataLen), cw.AsSpan(dataLen));
        return cw;
    }

    [Fact]
    public void ErasuresBeyondErrorCapacity_Decode()
    {
        const int nsym = 16;
        var rs = new ReedSolomon();
        byte[] clean = MakeCodeword(100, nsym, 1);
        var damaged = (byte[])clean.Clone();

        // 14 corruptions: double the errors-only capacity of 8 — but with known positions,
        // capacity is nsym = 16 erasures.
        int[] positions = [3, 9, 17, 20, 33, 41, 47, 58, 66, 71, 84, 90, 101, 110];
        foreach (int p in positions)
            damaged[p] ^= 0x5A;

        Assert.False(rs.TryDecode((byte[])damaged.Clone(), nsym, out _));
        Assert.True(rs.TryDecodeWithErasures(damaged, nsym, positions, out int corrected));
        Assert.Equal(14, corrected);
        Assert.Equal(clean, damaged);
    }

    [Fact]
    public void MixedErrorsAndErasures_WithinCapacity_Decode()
    {
        const int nsym = 16;
        var rs = new ReedSolomon();
        byte[] clean = MakeCodeword(100, nsym, 2);
        var damaged = (byte[])clean.Clone();

        int[] flagged = [5, 12, 30, 44, 52, 60, 77, 83, 95, 100, 104, 111]; // 12 erasures
        foreach (int p in flagged)
            damaged[p] ^= 0x33;
        damaged[20] ^= 0x77; // plus 2 unflagged errors: 2*2 + 12 = 16 = nsym exactly
        damaged[70] ^= 0x77;

        Assert.True(rs.TryDecodeWithErasures(damaged, nsym, flagged, out _));
        Assert.Equal(clean, damaged);
    }

    [Fact]
    public void BeyondErasureCapacity_Fails()
    {
        const int nsym = 16;
        var rs = new ReedSolomon();
        byte[] damaged = MakeCodeword(100, nsym, 3);

        var flagged = Enumerable.Range(0, 15).Select(i => i * 7).ToArray(); // 15 erasures
        foreach (int p in flagged)
            damaged[p] ^= 0x11;
        damaged[113] ^= 0x11; // + 1 error: 2 + 15 = 17 > 16

        Assert.False(rs.TryDecodeWithErasures(damaged, nsym, flagged, out _));
    }

    [Fact]
    public void FlaggingCorrectSymbols_IsHarmless()
    {
        const int nsym = 16;
        var rs = new ReedSolomon();
        byte[] clean = MakeCodeword(100, nsym, 4);
        var damaged = (byte[])clean.Clone();
        damaged[40] ^= 0xFF; // 1 real error

        // 10 flags on symbols that are actually FINE, plus the real error unflagged.
        var flagged = new[] { 1, 8, 15, 22, 29, 36, 50, 57, 64, 71 };
        Assert.True(rs.TryDecodeWithErasures(damaged, nsym, flagged, out int corrected));
        Assert.Equal(1, corrected); // only the real error had a nonzero magnitude
        Assert.Equal(clean, damaged);
    }

    /// <summary>
    /// The end-to-end payoff: damage sized BETWEEN the errors-only capacity (parity/2 = 8
    /// bytes/codeword) and the erasure capacity (~14/codeword). A full-width gray strip keeps
    /// the per-codeword damage uniform, so the A/B is deterministic: errors-only fails,
    /// erasure-flagged succeeds.
    /// </summary>
    [Fact]
    public void AmbiguousDamage_BetweenErrorAndErasureCapacity_DecodesViaErasures()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        // Read the layout to size the damage strip: target ~10 damaged bytes per codeword.
        var probe = new ShardDecoder().Diagnose(result.Files[0]);
        Assert.NotNull(probe.Layout);
        var layout = probe.Layout!;
        const int x0 = 60, x1 = 840;
        int coveredCellsPerRow = (x1 - x0) / layout.CellPx;
        int stripRows = Math.Max(1, (int)Math.Round(
            10.0 * 2 * layout.CodewordCount / coveredCellsPerRow)); // bytes→cells at 4 bits/cell
        int stripPx = stripRows * layout.CellPx;

        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            for (int y = 450 - stripPx / 2; y < 450 + (stripPx + 1) / 2; y++)
                for (int x = x0; x < x1; x++)
                    img[x, y] = new Rgb24(128, 128, 128); // gray: far from every palette color → flagged
            img.SaveAsPng(result.Files[0]);
        }

        // A/B at the FEC layer: same sampled cells, with and without the suspicion flags.
        var scratch = new DecodeScratch();
        Assert.True(new FastPngReader().TryRead(result.Files[0], scratch, out Bitmap bmp));
        var (foundLayout, inner) = new FrameLocator(new InnerRectScanner(), new StripReader()).Locate(bmp, scratch);
        var palettes = new StripReader().ReadPalette(bmp, inner, foundLayout);
        byte[] cells = new GridSampler().ReadDataGrid(bmp, inner, foundLayout, palettes, scratch, out bool[]? suspects);
        Assert.NotNull(suspects);

        var fec = new Fec();
        var dest = new byte[foundLayout.CodewordCount * Fec.DataLength(foundLayout.EccParity)];
        Assert.False(fec.TryRecoverInto(cells, foundLayout.EccParity, foundLayout.CodewordCount, dest, out _));
        Assert.True(fec.TryRecoverInto(cells, foundLayout.EccParity, foundLayout.CodewordCount, dest, out int corrected,
            null, suspects));
        Assert.True(corrected > 0);

        // And the full pipeline decodes the file bit-for-bit.
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([result.Files[0]], output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }
}
