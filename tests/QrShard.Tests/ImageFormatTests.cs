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
        Assert.Throws<ArgumentException>(() => ShardImageFormat.Normalize(format));

    [Fact]
    public void TifAlias_NormalizesToTiff() =>
        Assert.Equal("tiff", ShardImageFormat.Normalize("TIF"));

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
        FastPng.Write(path, pixels, w, h, upFilter, level);

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
            FastPng.Write(path, pixels, w, h, upFilter: true, System.IO.Compression.CompressionLevel.Optimal);

            using var decoded = Image.Load<Rgb24>(path);
            var roundTripped = new Rgb24[w * h];
            decoded.CopyPixelDataTo(roundTripped);
            Assert.Equal(pixels, roundTripped);
        }
    }
}
