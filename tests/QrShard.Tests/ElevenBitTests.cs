using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Metadata version 6 on a pixel-perfect 4K grid, and the row packer's lazy runner-up stream.
/// </summary>
public class ElevenBitTests
{
    [Fact]
    public void FourK_ElevenBits_RoundTrips_AndMatchesLayoutCapacity()
    {
        var layout = Layout.Create(3840, 2160, 1, 11, 16);
        Assert.Equal(11, layout.BitsPerCell);
        Assert.Equal(1, layout.CellPx);
        Assert.False(layout.CameraFinders);
        Assert.Equal(Layout.MetaVersionDeepCell, layout.PackMetadata()[1] >> 4);

        // Usable payload is the codeword formula on whatever grid Layout produced.
        long usable = (long)layout.CodewordCount * Fec.DataLength(layout.EccParity);
        Assert.Equal(usable, layout.UsableBytes);
        Assert.Equal(layout.TotalBytes / Fec.CodewordLength, layout.CodewordCount);

        // Reference arithmetic for 3840×2160, 1 px, no finder bands: border 28, metaH 38.
        Assert.Equal(3708, layout.GridW);
        Assert.Equal(1876, layout.GridH);
        Assert.Equal(8_964_412, layout.UsableBytes);

        using var tmp = new TempDir();
        byte[] content = TestData.Random(48_000, seed: 11);
        var opt = new EncodeOptions
        {
            Width = 3840,
            Height = 2160,
            CellPx = 1,
            BitsPerCell = 11,
            EccParity = 16,
            Compress = false,
        };
        string input = tmp.WriteFile("eleven.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), opt);
        Assert.Equal(layout.UsableBytes - ShardHeader.Size("eleven.bin"), result.BytesPerImage);
        string output = tmp.File("restored.bin");
        var restored = new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Single(restored);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void PackedExactRow_LeavesTheRunnerUpStreamUnwritten()
    {
        var palette = new Palette().Build(8);
        Assert.Equal(new Rgb24(0, 0, 85), palette[1]);
        var layout = new Layout
        {
            BitsPerCell = 8,
            CellPx = 1,
            GridW = 4,
            GridH = 1,
            MetaH = 1,
            InnerW = 2 + 4,
            InnerH = 6 + 1,
            EccParity = 16,
            FinderModule = 0,
        };
        int w = layout.InnerW;
        var px = new Rgb24[w * layout.InnerH];
        for (int gx = 0; gx < layout.GridW; gx++)
            px[3 * w + 1 + gx] = palette[1];

        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, layout.InnerH), new InnerRect(0, 0, w, layout.InnerH), layout,
            new PaletteSet(palette, palette, palette, Interpolate: false),
            new DecodeScratch(), out bool[]? suspects, out byte[]? second);

        Assert.Equal(new byte[] { 1, 1, 1, 1 }, stream);
        Assert.NotNull(suspects);
        Assert.All(suspects, flagged => Assert.False(flagged));
        Assert.NotNull(second);
        Assert.Equal(new byte[stream.Length], second);
    }

    [Fact]
    public void PackedElevenBitRow_MatchesTheCellStream()
    {
        var palette = new Palette().Build(11);
        int[] indices = [0, 1, 8, 128, 200, 511, 1000, 2047, 42];
        var layout = new Layout
        {
            BitsPerCell = 11,
            CellPx = 1,
            GridW = indices.Length,
            GridH = 1,
            MetaH = 1,
            InnerW = 2 + indices.Length,
            InnerH = 6 + 1,
            EccParity = 16,
            FinderModule = 0,
        };
        int w = layout.InnerW;
        var px = new Rgb24[w * layout.InnerH];
        for (int gx = 0; gx < indices.Length; gx++)
            px[3 * w + 1 + gx] = palette[indices[gx]];

        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, layout.InnerH), new InnerRect(0, 0, w, layout.InnerH), layout,
            new PaletteSet(palette, palette, palette, Interpolate: false),
            new DecodeScratch(), out _, out byte[]? second);

        var reference = new byte[stream.Length];
        var bits = new BitStream();
        for (int i = 0; i < indices.Length; i++)
            bits.WriteCell(reference, (long)i * 11, 11, indices[i]);
        Assert.Equal(reference, stream);
        Assert.NotNull(second);
        Assert.Equal(new byte[stream.Length], second);
    }

    [Fact]
    public void SuspectCell_KeepsTheConfidentNeighbourAcrossTheRowBoundary()
    {
        // One 10-bit cell per row leaves a 2-bit tail, so the rows share a byte. The first cell
        // is a near-tie; the second is an exact colour whose high bits sit in that shared byte.
        var palette = new Palette().Build(10);
        const int neighbour = 512; // 0b10_0000_0000, so the shared byte is not zero
        Assert.Equal(new Rgb24(136, 0, 0), palette[neighbour]);
        var layout = new Layout
        {
            BitsPerCell = 10,
            CellPx = 1,
            GridW = 1,
            GridH = 2,
            MetaH = 1,
            InnerW = 3,
            InnerH = 8,
            EccParity = 16,
            FinderModule = 0,
        };
        int w = layout.InnerW;
        var px = new Rgb24[w * layout.InnerH];
        px[3 * w + 1] = new Rgb24(8, 0, 0);
        px[4 * w + 1] = palette[neighbour];

        byte[] stream = new GridSampler().ReadDataGrid(
            new Bitmap(px, w, layout.InnerH), new InnerRect(0, 0, w, layout.InnerH), layout,
            new PaletteSet(palette, palette, palette, Interpolate: false),
            new DecodeScratch(), out bool[]? suspects, out byte[]? second);

        var bits = new BitStream();
        Assert.Equal(0, bits.ReadCell(stream, 0, 10));
        Assert.Equal(neighbour, bits.ReadCell(stream, 10, 10));
        Assert.NotNull(suspects);
        Assert.True(suspects[0]);
        Assert.True(suspects[1]);
        Assert.NotNull(second);

        // Chase splices whole flagged bytes. Those bytes must carry the runner-up and any
        // confident neighbour that shares them. Bytes the suspect cell does not touch stay 0.
        var spliced = (byte[])stream.Clone();
        bits.WriteCell(spliced, 0, 10, 64);
        for (int i = 0; i < second.Length; i++)
        {
            if (i < suspects.Length && suspects[i])
                Assert.Equal(spliced[i], second[i]);
            else
                Assert.Equal(0, second[i]);
        }
        Assert.Equal(64, bits.ReadCell(second, 0, 10));
        Assert.Equal(neighbour, bits.ReadCell(second, 10, 10));
    }
}
