using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard.Tests;

/// <summary>
/// Real capture shapes. Screenshots arrive from unknown OS tools that may not emit the 8-bit
/// truecolor PNG the FastPngReader fast-paths — palette, grayscale, 16-bit, interlaced. Those
/// must fall through to ImageSharp and still decode. Prior tests only ever re-read our own
/// FastPng output, so this fallback branch was structurally untested end to end.
/// </summary>
public class CaptureShapeTests
{
    private static string ReencodeShard(string source, string dest, PngEncoder encoder)
    {
        using var img = Image.Load<Rgb24>(source);
        img.Save(dest, encoder);
        return dest;
    }

    public static IEnumerable<object[]> NonTruecolorEncoders =>
    [
        ["palette", new PngEncoder { ColorType = PngColorType.Palette, BitDepth = PngBitDepth.Bit8 }],
        ["rgb16", new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit16 }],
        ["interlaced", new PngEncoder { InterlaceMethod = PngInterlaceMode.Adam7 }],
        ["rgba", new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 }],
    ];

    [Theory]
    [MemberData(nameof(NonTruecolorEncoders))]
    public void NonTruecolorPngCaptures_FallBackToImageSharp_AndDecode(string label, PngEncoder encoder)
    {
        using var tmp = new TempDir();
        // 2 bits/cell keeps the palette small enough that a palette-PNG re-encode stays lossless.
        byte[] content = TestData.Random(6_000, seed: label.Length);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 4, BitsPerCell = 2 });

        string capDir = tmp.Sub($"cap-{label}");
        var reencoded = result.Files
            .Select(f => ReencodeShard(f, Path.Combine(capDir, Path.GetFileName(f)), encoder))
            .ToList();

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(reencoded, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void OddDimensionCapture_WithPadding_Decodes()
    {
        // A capture padded to odd width/height (a hand-cropped screenshot) must still locate the
        // frame and decode — exercises the non-even geometry the simulated captures skip.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(8_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });

        string padded = tmp.File("padded.png");
        using (var shard = Image.Load<Rgb24>(result.Files[0]))
        {
            using var canvas = new Image<Rgb24>(shard.Width + 37, shard.Height + 51, new Rgb24(238, 240, 243));
            canvas.Mutate(c => c.DrawImage(shard, new Point(19, 23), 1f));
            canvas.SaveAsPng(padded);
        }

        var decoded = new ShardDecoder().DecodeImage(padded, new DecodeScratch());
        Assert.Equal(0, decoded.Header.Index);
    }
}
