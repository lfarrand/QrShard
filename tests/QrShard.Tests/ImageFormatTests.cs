using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Configurable lossless container formats and the built-in fast PNG writer.</summary>
public class ImageFormatTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("tga")]
    [InlineData("qoi")]
    [InlineData("webp")]
    [InlineData("tiff")]
    public void EveryFormat_RoundTripsByteIdentical(string format)
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(25_000, seed: format.GetHashCode());
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { ImageFormat = format });

        Assert.All(result.Files, f => Assert.EndsWith("." + format, f));

        string output = tmp.File("restored.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("qoi")]
    [InlineData("bmp")]
    public void NonPngFormats_SurviveSimulatedCaptureDamage(string format)
    {
        // The container is transport-only; per-image ECC must work identically through it.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000, seed: 9);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { ImageFormat = format });

        string damaged = tmp.File("damaged." + format);
        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            img.ProcessPixelRows(acc =>
            {
                for (int y = img.Height / 2; y < img.Height / 2 + 20; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = img.Width / 2; x < img.Width / 2 + 20; x++)
                        row[x] = new Rgb24(255, 255, 255);
                }
            });
            img.Save(damaged);
        }

        var shard = new ShardDecoder().DecodeImage(damaged);
        Assert.True(shard.CorrectedBytes > 0);
        Assert.Equal(content[..shard.Payload.Length], shard.Payload);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("jpeg")]
    [InlineData("avif")]
    public void UnsupportedFormats_AreRejected(string format) =>
        Assert.Throws<ArgumentException>(() => new ShardImageFormat().Normalize(format));

    [Fact]
    public void TifAlias_NormalizesToTiff() =>
        Assert.Equal("tiff", new ShardImageFormat().Normalize("TIF"));

    // ---------- FastPng: our own PNG writer must be standard-compliant and lossless ----------

    [Theory]
    [InlineData(true, System.IO.Compression.CompressionLevel.Optimal)]
    [InlineData(true, System.IO.Compression.CompressionLevel.Fastest)]
    [InlineData(true, System.IO.Compression.CompressionLevel.SmallestSize)]
    [InlineData(true, System.IO.Compression.CompressionLevel.NoCompression)]
    [InlineData(false, System.IO.Compression.CompressionLevel.Fastest)]
    [InlineData(false, System.IO.Compression.CompressionLevel.Optimal)]
    public void FastPng_DecodesBackPixelIdentical(bool upFilter, System.IO.Compression.CompressionLevel level)
    {
        using var tmp = new TempDir();
        const int w = 137, h = 61; // deliberately odd sizes
        var pixels = new Rgb24[w * h];
        var rng = new Random(upFilter ? 1 : 2);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));

        string path = tmp.File("out.png");
        new FastPng().Write(path, pixels, w, h, upFilter, level);

        using var decoded = Image.Load<Rgb24>(path);
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
        var roundTripped = new Rgb24[w * h];
        decoded.CopyPixelDataTo(roundTripped);
        Assert.Equal(pixels, roundTripped);
    }

    [Fact]
    public void FastPng_SingleRowAndSingleColumn_Work()
    {
        using var tmp = new TempDir();
        foreach (var (w, h) in new[] { (1, 50), (50, 1), (1, 1) })
        {
            var pixels = new Rgb24[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Rgb24((byte)(i * 7), (byte)(i * 13), (byte)(i * 29));
            string path = tmp.File($"tiny-{w}x{h}.png");
            new FastPng().Write(path, pixels, w, h, upFilter: true, System.IO.Compression.CompressionLevel.Optimal);

            using var decoded = Image.Load<Rgb24>(path);
            var roundTripped = new Rgb24[w * h];
            decoded.CopyPixelDataTo(roundTripped);
            Assert.Equal(pixels, roundTripped);
        }
    }

    /// <summary>
    /// 1 px cells are the only configuration that takes the stored-deflate PNG path, and so the
    /// only one that reaches our hand-rolled zlib stream and its Adler-32. Every other round-trip
    /// test uses cells >= 2 px and therefore goes through ZLibStream instead.
    /// </summary>
    [Fact]
    public void OnePixelCells_TakeTheStoredZlibPath_AndRoundTripByteIdentical()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000, seed: 91);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 1280, Height = 720, CellPx = 1, BitsPerCell = 6 });

        // Every IDAT must inflate cleanly, which validates the Adler-32 we wrote ourselves.
        foreach (string file in result.Files)
        {
            using var inflate = new System.IO.Compression.ZLibStream(
                new MemoryStream(ExtractChunk(File.ReadAllBytes(file), "IDAT")),
                System.IO.Compression.CompressionMode.Decompress);
            inflate.CopyTo(Stream.Null);
        }

        string output = tmp.File("restored.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    // ---------- Adler-32: the zlib trailer. A wrong value makes every decoder reject the PNG ----------

    /// <summary>The definition, one byte at a time — deliberately naive, nothing shared with the writer.</summary>
    private static uint ReferenceAdler(ReadOnlySpan<byte> data)
    {
        uint s1 = 1, s2 = 0;
        foreach (byte b in data)
        {
            s1 = (s1 + b) % 65521;
            s2 = (s2 + s1) % 65521;
        }
        return (s2 << 16) | s1;
    }

    /// <summary>
    /// The SIMD fold works in groups of 23 vectors, so the interesting lengths are the ones that
    /// straddle a group boundary for every vector width the runtime might pick (16/32/64 bytes),
    /// the scalar NMax fold, and the writer's 64 KB stored-block size.
    /// </summary>
    [Fact]
    public void Adler32_MatchesScalarReference_AcrossLengthsAndContent()
    {
        var lengths = new HashSet<int>();
        for (int n = 0; n <= 70; n++)
            lengths.Add(n);
        foreach (int boundary in new[] { 23 * 16, 23 * 32, 23 * 64, 5552, 11_104, 65_535, 65_536 })
            for (int d = -3; d <= 3; d++)
                lengths.Add(boundary + d);
        var lengthRng = new Random(4242);
        for (int i = 0; i < 250; i++)
            lengths.Add(lengthRng.Next(200_001));

        foreach (int length in lengths)
        {
            var rng = new Random(length + 1);
            byte[][] patterns =
            [
                Fill(length, _ => (byte)rng.Next(256)),
                Fill(length, _ => byte.MaxValue), // worst case for the lane accumulators
                Fill(length, _ => (byte)0),
                Fill(length, i => (byte)i),       // position-dependent: catches lane misordering
            ];
            foreach (byte[] data in patterns)
            {
                uint s1 = 1, s2 = 0;
                FastPng.UpdateAdler(ref s1, ref s2, data);
                Assert.Equal(ReferenceAdler(data), (s2 << 16) | s1);
            }
        }

        static byte[] Fill(int n, Func<int, byte> f)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
                b[i] = f(i);
            return b;
        }
    }

    [Fact]
    public void Adler32_IsIdenticalWhenFedInPieces()
    {
        var rng = new Random(7);
        var data = new byte[200_000];
        rng.NextBytes(data);
        uint expected = ReferenceAdler(data);

        for (int trial = 0; trial < 50; trial++)
        {
            uint s1 = 1, s2 = 0;
            int at = 0;
            while (at < data.Length)
            {
                int n = Math.Min(rng.Next(1, 90_000), data.Length - at);
                FastPng.UpdateAdler(ref s1, ref s2, data.AsSpan(at, n));
                at += n;
            }
            Assert.Equal(expected, (s2 << 16) | s1);
        }
    }

    /// <summary>
    /// Independent oracle: the zlib implementation shipped with the runtime. (Length 0 is not
    /// among the cases because ZLibStream emits no bytes at all when nothing is written; the
    /// empty input is covered against the reference above.)
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(5552)]
    [InlineData(70_000)]
    [InlineData(250_001)]
    public void Adler32_MatchesZLibTrailer(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
                   ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data);
        byte[] stream = ms.ToArray();
        uint zlibAdler = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            stream.AsSpan(stream.Length - 4));

        uint s1 = 1, s2 = 0;
        FastPng.UpdateAdler(ref s1, ref s2, data);
        Assert.Equal(zlibAdler, (s2 << 16) | s1);
    }

    /// <summary>
    /// End-to-end: inflate the stored-mode IDAT with the runtime's zlib, which validates the
    /// Adler-32 trailer and throws on mismatch — so this fails loudly if the writer's checksum
    /// is wrong, where a pixel comparison through a lenient decoder would not.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(137, 61)]
    [InlineData(1920, 33)]   // > 64 KB of stored blocks
    [InlineData(3840, 9)]    // Max4K row width
    public void FastPng_StoredZlibStream_PassesZLibChecksumValidation(int w, int h)
    {
        using var tmp = new TempDir();
        var pixels = new Rgb24[w * h];
        var rng = new Random(w * h);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));

        string path = tmp.File($"stored-{w}x{h}.png");
        new FastPng().Write(path, pixels, w, h, upFilter: false,
            System.IO.Compression.CompressionLevel.Fastest);

        byte[] png = File.ReadAllBytes(path);
        byte[] idat = ExtractChunk(png, "IDAT");
        using var raw = new MemoryStream();
        using (var inflate = new System.IO.Compression.ZLibStream(
                   new MemoryStream(idat), System.IO.Compression.CompressionMode.Decompress))
            inflate.CopyTo(raw); // throws InvalidDataException if the Adler-32 does not match

        byte[] scanlines = raw.ToArray();
        Assert.Equal((w * 3 + 1) * h, scanlines.Length);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, scanlines[y * (w * 3 + 1)]); // filter: None
            for (int x = 0; x < w; x++)
            {
                int at = y * (w * 3 + 1) + 1 + x * 3;
                Assert.Equal(pixels[y * w + x], new Rgb24(scanlines[at], scanlines[at + 1], scanlines[at + 2]));
            }
        }
    }

    private static byte[] ExtractChunk(byte[] png, string type)
    {
        var body = new MemoryStream();
        int at = 8; // past the signature
        while (at + 8 <= png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string name = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (name == type)
                body.Write(png, at + 8, length);
            at += 12 + length;
        }
        return body.ToArray();
    }
}
