using System.Buffers.Binary;
using System.IO.Compression;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The fast PNG reader sizes its pixel buffer from the header's DECLARED dimensions, before any
/// pixel data is read. A few hundred bytes of crafted IHDR must not therefore buy a
/// hundreds-of-megabytes allocation.
/// </summary>
public class PngAllocationGuardTests
{
    private static byte[] BuildPng(int width, int height, byte[] idatPayload)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(data);
            // The reader skips CRCs, so any four bytes will do here.
            ms.Write(new byte[4]);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour RGB
        Chunk("IHDR", ihdr);
        Chunk("IDAT", idatPayload);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    [Fact]
    public void DeclaredDimensionsFarBeyondTheIdat_AreRejectedWithoutAllocating()
    {
        using var tmp = new TempDir();
        // ~300 bytes of IDAT claiming a 20000x20000 image: 1.2 GB of raw pixels, which no
        // 300-byte deflate stream can produce (the ratio ceiling is 1032:1).
        string path = tmp.WriteFile("bomb.png", BuildPng(20_000, 20_000, new byte[300]));

        var scratch = new DecodeScratch();
        Assert.False(new FastPngReader().TryRead(path, scratch, out _),
            "a header declaring far more pixels than the IDAT could hold must be refused");
    }

    [Fact]
    public void AGenuineImage_IsStillAccepted()
    {
        using var tmp = new TempDir();
        // A real 64x64 RGB image: filter byte + 3 bytes per pixel per row, actually compressed.
        const int w = 64, h = 64;
        var raw = new byte[(w * 3 + 1) * h];
        new Random(3).NextBytes(raw);
        for (int y = 0; y < h; y++)
            raw[y * (w * 3 + 1)] = 0; // filter: None

        string path = tmp.WriteFile("real.png", BuildPng(w, h, Deflate(raw)));

        var scratch = new DecodeScratch();
        Assert.True(new FastPngReader().TryRead(path, scratch, out var bitmap),
            "the ratio guard must not reject an image that genuinely carries its pixels");
        Assert.Equal(w, bitmap.Width);
        Assert.Equal(h, bitmap.Height);
    }

    [Fact]
    public void HighlyCompressibleRealImage_IsNotRejected()
    {
        using var tmp = new TempDir();
        // The guard's worst case for false positives: a large, extremely compressible image whose
        // IDAT is genuinely tiny. All-zero pixels compress far better than typical content, so if
        // the 1032:1 ceiling were set too tight this is what would break.
        const int w = 512, h = 512;
        var raw = new byte[(w * 3 + 1) * h]; // all zeroes, filter None throughout
        string path = tmp.WriteFile("flat.png", BuildPng(w, h, Deflate(raw)));

        var scratch = new DecodeScratch();
        Assert.True(new FastPngReader().TryRead(path, scratch, out _),
            "a legitimately tiny IDAT for a flat image must still be accepted");
    }
}
