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

    [Fact]
    public void UnknownMetadataVersion_IsRejectedRatherThanParsedAsV4()
    {
        // SPEC section 2.2 requires unknown versions to be rejected — that nibble is the format's
        // capability field and the reason older builds refuse a v4 strip. UnpackV4 checked neither
        // magic nor version after correcting, and the comments on those two lines claimed the
        // caller had already matched them. It had not: dispatch reaches UnpackV4 precisely BECAUSE
        // the raw bytes did not look like v2 or v3, which is what allows a damaged version to be
        // repaired. So a future version 5 strip whose CRC verified would have been silently parsed
        // as v4 — fields read at the wrong offsets, geometry wrong, and no error.
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        byte[] strip = layout.PackMetadata();
        Assert.NotNull(Layout.UnpackMetadata(ToModules(strip)));

        // Re-stamp the version nibble as 5 and repair the CRC and parity so the strip is
        // internally perfect — only the version is unknown.
        byte[] forged = ForgeVersion(strip, 5);
        Assert.Null(Layout.UnpackMetadata(ToModules(forged)));

        // And a wrong magic is refused too, for the same reason.
        byte[] badMagic = (byte[])strip.Clone();
        badMagic[0] = 0x00;
        Reseal(badMagic);
        Assert.Null(Layout.UnpackMetadata(ToModules(badMagic)));
    }

    /// <summary>Rewrites the version nibble, then rebuilds CRC and RS parity over the result.</summary>
    private static byte[] ForgeVersion(byte[] strip, int version)
    {
        var f = (byte[])strip.Clone();
        f[1] = (byte)((f[1] & 0x0F) | (version << 4));
        Reseal(f);
        return f;
    }

    /// <summary>Recomputes the CRC over the 9 field bytes and the RS parity over the 11 that follow.</summary>
    private static void Reseal(byte[] strip)
    {
        ushort crc = new Crc().Crc16Ccitt(strip.AsSpan(0, 9));
        strip[9] = (byte)(crc >> 8);
        strip[10] = (byte)crc;
        new ReedSolomon().Encode(strip.AsSpan(0, 11), strip.AsSpan(11, 5));
    }

    private static bool[] ToModules(byte[] packed)
    {
        var m = new bool[Layout.MetaModuleCount];
        for (int i = 0; i < m.Length; i++)
            m[i] = (packed[i >> 3] & (0x80 >> (i & 7))) != 0;
        return m;
    }

    [Fact]
    public void TwoArchivesSharingAHeaderName_DoNotOverwriteEachOther()
    {
        // Every multi-input encode is named "bundle", so any two `qrshard encode a b c` sets
        // decoded together shared a destination — and ExtractTar writes with overwrite: true, so
        // the later group silently replaced the earlier one's files. The single-file path had
        // counted since the last round; this one stopped two lines short of it.
        using var tmp = new TempDir();
        string cwd = tmp.Sub("work");
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd;
            var shards = new List<DecodedShard>
            {
                ArchiveShard("bundle.tar", "first.txt", "one", fileId: 0x1111),
                ArchiveShard("bundle.tar", "second.txt", "two", fileId: 0x2222),
            };
            new ShardAssembler().Assemble(shards, null, _ => { });
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        // Both payloads survived, in separate directories.
        var found = Directory.GetFiles(cwd, "*.txt", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        Assert.Contains("one", found);
        Assert.Contains("two", found);
    }

    private static DecodedShard ArchiveShard(string headerName, string entryName, string content, ulong fileId)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Formats.Tar.TarWriter(ms, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            var e = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, entryName);
            var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            e.DataStream = body;
            w.WriteEntry(e);
        }
        byte[] tar = ms.ToArray();
        var header = new ShardHeader
        {
            FileId = fileId, Index = 0, Count = 1,
            PayloadLength = tar.Length, PayloadCrc32 = new Crc().Crc32(tar),
            TotalLength = tar.Length, OriginalLength = tar.Length,
            Flags = ShardHeader.FlagArchive,
            Sha256 = System.Security.Cryptography.SHA256.HashData(tar),
            FileName = headerName, StripeData = 0, StripeParity = 0,
        };
        return new DecodedShard(header, tar, "crafted.png", 0, 0);
    }

    [Fact]
    public void HostileFileNamesAreNeutralisedInEveryDecodeMessage()
    {
        // Display was applied at eleven sites and missed eight, including the two users hit
        // most: the wrong-password message and the beyond-parity-recovery one. Those print
        // verbatim through the CLI's `error: {ex.Message}` handler, and a terminal is an
        // interpreter — a bare CR overwrites the line just printed, and an escape sequence can
        // rewrite earlier output entirely.
        string hostile = "a\r\u001b[2Kevil.bin";
        string shown = ShardHeader.Display(hostile);
        Assert.DoesNotContain(shown, c => char.IsControl(c));
        Assert.Contains("evil.bin", shown);   // the readable part survives

        var ex = Record.Exception(() => new PayloadCipher().DecryptInPlace(
            new byte[8], "wrong-password", hostile, default));
        Assert.IsType<ShardDecodeException>(ex);
        Assert.DoesNotContain(ex!.Message, c => char.IsControl(c));
        Assert.Contains("evil.bin", ex.Message);
    }

    [Fact]
    public void ASingleUnreadableImage_SurfacesAsATypedFailureNotACrash()
    {
        // The seventh instance of the same pattern. The previous round broadened the exception
        // filter in the worker-sizing probe, and argued in its own comment that enumerating types
        // is the wrong policy because Image.Identify inflates ancillary chunks and raises
        // System.IO.InvalidDataException, which derives from SystemException rather than
        // IOException. That argument applies verbatim to the two ImageSharp entry points sixty and
        // a hundred and fifty lines away in the same file, and neither was touched.
        //
        // LoadBitmap's own comment states the contract: these "must all surface as the typed
        // decode failure - so the session API returns an error result ... never leak a raw
        // exception". InvalidDataException broke it, so `qrshard info`, `qrshard calibrate` and
        // QrShardDecodeSession.AddImage all died with a stack trace on one crafted file.
        //
        // The batch path was already safe, via the blanket per-image catch in CollectShards -
        // which is why the previous round's test passed while both single-image paths crashed.
        using var tmp = new TempDir();
        string path = tmp.File("evil.png");
        File.WriteAllBytes(path, PngWithCorruptZtxt());

        var fromFile = Record.Exception(() => new ShardDecoder().DecodeImage(path));
        Assert.IsType<ShardDecodeException>(fromFile);

        var fromBytes = Record.Exception(() => new QrShardDecodeSession().AddImageBytes(PngWithCorruptZtxt()));
        Assert.Null(fromBytes); // the session API reports, it does not throw
    }

    [Fact]
    public void TheSessionApiReportsAnUnreadableImageRatherThanThrowing()
    {
        var result = new QrShardDecodeSession().AddImageBytes(PngWithCorruptZtxt());
        Assert.False(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
