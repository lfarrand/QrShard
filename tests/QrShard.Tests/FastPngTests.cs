using System.IO.Compression;
using System.Text;
using QrShard;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

public class FastPngTests
{
    [Fact]
    public void Write_TinyRgbPng_IhdrMethodBytesAreZero()
    {
        using var tmp = new TempDir();
        var pixels = new Rgb24[] { new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 255) };
        string path = tmp.File("tiny.png");

        new FastPng().Write(path, pixels, width: 2, height: 2, upFilter: false, CompressionLevel.Fastest);

        byte[] png = File.ReadAllBytes(path);
        Assert.True(png.Length >= 16 + 13, "PNG must contain an IHDR payload");
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        ReadOnlySpan<byte> ihdr = png.AsSpan(16, 13);
        Assert.Equal(0, ihdr[10]);
        Assert.Equal(0, ihdr[11]);
        Assert.Equal(0, ihdr[12]);
    }
}
