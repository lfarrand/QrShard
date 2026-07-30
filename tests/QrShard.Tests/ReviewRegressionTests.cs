using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Findings from the adversarial review of 1.6.0, all against code added in that same session.
/// Both defects share a shape worth naming: a guard was written against the case its author had
/// in mind, and the neighbouring case — one strip instead of both, one exception type instead of
/// the family — went the other way.
/// </summary>
public class ReviewRegressionTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
                c = (c >> 1) ^ (0xEDB88320u & (uint)(-(c & 1)));
        }
        return ~c;
    }

    private static void Chunk(List<byte> o, string type, byte[] data)
    {
        o.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        var body = new List<byte>(System.Text.Encoding.ASCII.GetBytes(type));
        body.AddRange(data);
        o.AddRange(body);
        uint crc = Crc32(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(body));
        o.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    /// <summary>An 88-byte PNG whose zTXt chunk carries a malformed deflate stream. Identify
    /// inflates ancillary text, so this raises System.IO.InvalidDataException.</summary>
    private static byte[] PngWithCorruptZtxt()
    {
        var o = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(o, "IHDR", [0, 0, 0, 100, 0, 0, 0, 100, 8, 2, 0, 0, 0]);
        var z = new List<byte>(System.Text.Encoding.ASCII.GetBytes("Comment")) { 0, 0 };
        z.AddRange([0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF]);
        Chunk(o, "zTXt", [.. z]);
        Chunk(o, "IDAT", [1, 2, 3, 4]);
        Chunk(o, "IEND", []);
        return [.. o];
    }

    [Fact]
    public void OneUnidentifiableFile_DoesNotKillTheWholeDecode()
    {
        // The worker-size probe runs BEFORE any worker, so it sits outside the per-image catch
        // that exists to stop one crafted file destroying a folder of good captures. Its filter
        // enumerated exception types and missed InvalidDataException, which derives from
        // SystemException rather than IOException — so the process died with a stack trace and
        // decoded nothing.
        //
        // Needs two or more images: at one, the probe short-circuits and never runs.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(100_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        string folder = tmp.Sub("incoming");
        foreach (string f in result.Files)
            File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
        // Sorted first, so it is probed before any good file.
        File.WriteAllBytes(Path.Combine(folder, "aaa_bad.png"), PngWithCorruptZtxt());

        // The probe short-circuits at a single file, so the crash needs two or more.
        Assert.True(Directory.GetFiles(folder).Length >= 2, "need 2+ files for the probe to run");

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(Directory.GetFiles(folder), restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    /// <summary>Paints one palette block in ONE strip, leaving the other copy pristine.</summary>
    private static void DamageOneStripBlock(Image<Rgb24> img, Layout layout, int block, Rgb24 colour, bool bottom)
    {
        int blocks = 1 << layout.BitsPerCell;
        int stripW = layout.InnerW - 2 * layout.Gutter;
        int blockW = stripW / blocks;
        int x = Layout.Border + layout.Gutter + block * blockW + blockW / 2;
        int bandY = bottom
            ? Layout.Border + layout.InnerH - layout.Gutter - 2 * layout.MetaH
            : Layout.Border + layout.Gutter + layout.MetaH;

        img.ProcessPixelRows(acc =>
        {
            for (int y = bandY; y < bandY + layout.MetaH && y < img.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int px = Math.Max(0, x - blockW / 3); px < Math.Min(img.Width, x + blockW / 3); px++)
                    row[px] = colour;
            }
        });
    }

    [Theory]
    [InlineData(true)]   // bottom strip obscured
    [InlineData(false)]  // top strip obscured — symmetric
    public void ObscuringOneCalibrationStripBlock_StillDecodes(bool bottom)
    {
        // A shadow, toast or finger over ONE strip is far likelier than the same mark hitting both
        // copies. It used to be fatal, while the SAME damage applied to BOTH strips decoded fine —
        // the redundancy inverted. The damaged strip passed the illumination gate (one bad block
        // out of sixteen barely moves a mean over 48 samples), and passing it switched
        // interpolation on, at which point the classifier lerped toward the damage and never
        // looked at the healthy copy that had been chosen as Best.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        var layout = Layout.Create(Fast.Width, Fast.Height, Fast.CellPx, Fast.BitsPerCell, Fast.EccParity);

        string damaged = tmp.File("damaged.png");
        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            DamageOneStripBlock(img, layout, block: (1 << Fast.BitsPerCell) - 1, colour: new Rgb24(170, 170, 170), bottom: bottom);
            img.SaveAsPng(damaged);
        }

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder([damaged], restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void AGenuineIlluminationGradient_StillInterpolates()
    {
        // The per-entry cap must not cost the feature it guards. A smooth vertical gain difference
        // between the two strips is exactly what interpolation exists for, and it has to survive:
        // rejecting it would throw away the colour tracking on every real photo capture.
        var palette = new Palette();
        var theoretical = palette.Build(4);
        var dim = new Rgb24[theoretical.Length];
        for (int i = 0; i < theoretical.Length; i++)
            dim[i] = new Rgb24((byte)(theoretical[i].R * 0.72), (byte)(theoretical[i].G * 0.72), (byte)(theoretical[i].B * 0.72));

        // Same shape, uniformly scaled: a pure gain, which is what illumination looks like.
        Assert.True(StripReader.FitsAsIlluminationForTests(dim, theoretical),
            "a uniformly dimmed strip must still read as illumination");

        // One block displaced by a shadow is not a gain, however small the mean says it is.
        var obscured = (Rgb24[])theoretical.Clone();
        obscured[^1] = new Rgb24(170, 170, 170);
        Assert.False(StripReader.FitsAsIlluminationForTests(obscured, theoretical),
            "a single displaced block must not read as illumination");
    }
}
