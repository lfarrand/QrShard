using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// Findings from the six-lens adversarial review of 1.6.0. Every one was reproduced by execution
/// before it was fixed, and every test here fails against the code as it stood at b466ca5.
///
/// Three of them are the same recurring shape this codebase keeps producing — a guard written for
/// the case its author pictured, with the neighbouring case going the other way — and two of those
/// three were found inside the fix for the previous instance of it.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class DeepReviewRegressionTests
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
    public void TheDecodeCommandSurvivesAnUnreadableImage()
    {
        // Instance 8, and the one that stings: the PREVIOUS round broadened three ImageSharp
        // filters in ShardDecoder and shipped a test proving `info` and `verify` were contained.
        // It never ran `decode` — the command the tool exists for — and decode reaches ImageSharp
        // through a FOURTH site the sweep missed. Cli dispatch calls VideoDecoder.IsAnimatedImage
        // on the raw path to decide whether the argument is an animation, before any decoder is
        // constructed, so it sits outside every net the last fix widened. The same 88-byte file
        // that `info` handled cleanly killed `decode` with an unhandled InvalidDataException.
        using var tmp = new TempDir();
        string path = tmp.File("evil.png");
        File.WriteAllBytes(path, PngWithCorruptZtxt());

        var @out = new StringWriter();
        var err = new StringWriter();
        int exit = 0;
        var thrown = Record.Exception(() => exit = new Cli().Run(["decode", path], @out, err, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(thrown);
        Assert.NotEqual(0, exit);
        Assert.Contains("not a readable image", (@out.ToString() + err).ToLowerInvariant());
    }

    [Fact]
    public void AnimatedFrameLoadingSurfacesTypedFailures()
    {
        // The fifth site, and it had no filter at all: RecordingFrameSource.AnimatedImageFrames
        // called Image.Load bare. Reached by decoding an animated capture, which is attacker-
        // supplied by exactly the same argument as every other decode input.
        using var tmp = new TempDir();
        string path = tmp.File("evil.gif");
        File.WriteAllBytes(path, PngWithCorruptZtxt());

        var thrown = Record.Exception(() => new RecordingFrameSource().Frames(path, 1, TestContext.Current.CancellationToken).ToList());
        Assert.IsType<ShardDecodeException>(thrown);
    }

    [Fact]
    public void IncompleteMixedFamilyPreflightsBeforeAnyOutput()
    {
        // A mixed folder is one decode request. Structural completeness must be established for
        // every family before any sibling is published, so group/filename order cannot produce a
        // misleading partially-successful result that a retry later silently renames.
        using var tmp = new TempDir();
        byte[] partialContent = TestData.Random(900_000); // spans 3 images at this density
        byte[] wholeContent = TestData.Random(20_000);    // fits in 1
        var enc = new ShardEncoder();
        var partial = enc.Encode(tmp.WriteFile("aaa_partial.bin", partialContent), tmp.Sub("a"), Fast);
        var whole = enc.Encode(tmp.WriteFile("zzz_whole.bin", wholeContent), tmp.Sub("z"), Fast);
        Assert.True(partial.Files.Count >= 2, "the partial file must span more than one image");

        // Only the FIRST image of the multi-image file, so its group can never be completed.
        string folder = tmp.Sub("mixed");
        File.Copy(partial.Files[0], Path.Combine(folder, Path.GetFileName(partial.Files[0])));
        File.Copy(whole.Files[0], Path.Combine(folder, Path.GetFileName(whole.Files[0])));

        string previous = Environment.CurrentDirectory;
        string cwd = tmp.Sub("out");
        try
        {
            Environment.CurrentDirectory = cwd;
            // One family genuinely cannot be rebuilt, so the entire mixed request fails before
            // the otherwise-complete sibling becomes externally visible.
            Assert.Throws<ShardDecodeException>(() =>
                new ShardDecoder().DecodeFolder(Directory.GetFiles(folder), null, _ => { }));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.False(File.Exists(Path.Combine(cwd, "zzz_whole.bin")));
    }

    /// <summary>A frame locator that reports a grid far finer than the area it claims to have
    /// found it in — the shape of every "lying strip" crafted image.</summary>
    private sealed class LyingLocator(Layout layout) : IFrameLocator
    {
        public (Layout Layout, InnerRect Inner) Locate(Bitmap bmp, DecodeScratch scratch) =>
            (layout, new InnerRect(0, 0, 40, 40));
    }

    /// <summary>Records whether the sampler was ever reached.</summary>
    private sealed class WatchingSampler : IGridSampler
    {
        public bool WasCalled { get; private set; }

        public byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes,
            DecodeScratch scratch, out bool[]? suspectBytes, out byte[]? secondChoiceBytes,
            int[]? cellMargins = null)
        {
            WasCalled = true;
            suspectBytes = null;
            secondChoiceBytes = null;
            return [];
        }
    }

    [Fact]
    public void AGridFinerThanItsCaptureIsRejectedBeforeAnythingIsSizedFromIt()
    {
        // GridSampler rejected this, so the decode always failed — but it failed too late. The
        // diagnostics path three statements EARLIER allocated int[GridW*GridH] and rendered a
        // GridW*6 x GridH*6 heatmap from the same unvalidated numbers: an 8.8 KB file declaring
        // 2000x2000 wrote a 12000x12000, 4.2 MB PNG to disk before the decoder called that very
        // layout physically impossible.
        //
        // So the assertion that matters is about ORDER, not about the rejection. The sampler must
        // never be reached: everything that sizes a buffer from the strip lives between the
        // locator and it.
        var lying = Layout.Create(900, 900, 3, 4, 8);
        var sampler = new WatchingSampler();
        var decoder = new ShardDecoder(
            AppSettings.Current, new CameraRectifier(), new LyingLocator(lying),
            new StripReader(), sampler, new ShardAssembler(),
            new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        var bmp = new Bitmap(new Rgb24[64 * 64], 64, 64);
        var thrown = Record.Exception(() => decoder.DecodeBitmap(bmp, new DecodeScratch(), "lying.png"));

        Assert.IsType<ShardDecodeException>(thrown);
        Assert.Contains("cannot resolve", thrown.Message);
        Assert.False(sampler.WasCalled, "the grid sampler was reached, so the check still runs too late");
    }

    [Fact]
    public void ADamagedVersionNibbleIsRepairedRatherThanMisrouted()
    {
        // The whole purpose of version 4 is that the strip survives a burst. But dispatch keyed on
        // the UNCORRECTED version nibble, so a burst that left the magic intact and turned the 4
        // into a 2 or a 3 handed the strip to the legacy parser — which has no parity — and the
        // five symbols that would have repaired that exact nibble never ran.
        var layout = Layout.Create(900, 900, 3, 4, 8, interleave2: false);
        byte[] strip = layout.PackMetadata();

        foreach (byte impostor in new byte[] { 2, 3 })
        {
            var damaged = (byte[])strip.Clone();
            // The version is the HIGH nibble of byte 1; the low nibble is bitsPerCell. Writing the
            // impostor into the low nibble instead corrupts bitsPerCell, leaves the version reading
            // 4, and the strip takes the v4 path and is repaired — a test that passes without
            // touching the code it was written for.
            damaged[1] = (byte)((damaged[1] & 0x0F) | (impostor << 4));
            Assert.Equal(impostor, (byte)(damaged[1] >> 4));

            var modules = new bool[Layout.MetaModuleCount];
            for (int i = 0; i < modules.Length; i++)
                modules[i] = (damaged[i >> 3] & (0x80 >> (i & 7))) != 0;

            var recovered = Layout.UnpackMetadata(modules);
            Assert.NotNull(recovered);
            Assert.Equal(layout.GridW, recovered.GridW);
            Assert.Equal(layout.GridH, recovered.GridH);
            Assert.Equal(layout.EccParity, recovered.EccParity);
        }
    }

    [Fact]
    public void TheEncoderGuardNamesTheFieldWidthTheFormatActuallyHas()
    {
        // Version 4 narrowed the dimension fields from 16 bits to 14 and Create's guard kept
        // saying ushort — a bound four times looser than the strip it writes into. BitWriter
        // truncates silently rather than throwing, so the failure mode if that headroom ever
        // closes is a corrupt strip, not an exception.
        Assert.Equal((1 << 14) - 1, Layout.MaxMetaField);

        // The real encoder cannot reach it today, and this pins the margin that makes that true.
        var largest = Layout.Create(Layout.MaxResolution, Layout.MaxResolution, 1, 4, 8);
        Assert.True(largest.GridW <= Layout.MaxMetaField, $"gridW {largest.GridW} exceeds the 14-bit field");
        Assert.True(largest.GridH <= Layout.MaxMetaField, $"gridH {largest.GridH} exceeds the 14-bit field");
        Assert.True(largest.MetaH <= Layout.MaxMetaField, $"metaH {largest.MetaH} exceeds the 14-bit field");

        // And it round-trips at that extreme, which is what the widths are for.
        var modules = new bool[Layout.MetaModuleCount];
        byte[] strip = largest.PackMetadata();
        for (int i = 0; i < modules.Length; i++)
            modules[i] = (strip[i >> 3] & (0x80 >> (i & 7))) != 0;
        var back = Layout.UnpackMetadata(modules);
        Assert.NotNull(back);
        Assert.Equal(largest.GridW, back.GridW);
        Assert.Equal(largest.GridH, back.GridH);
    }
}
