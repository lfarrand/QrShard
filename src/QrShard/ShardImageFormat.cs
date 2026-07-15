using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Qoi;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;

namespace QrShard;

/// <summary>
/// The lossless container formats a shard can be written as. Every format is bit-exact —
/// which the shard format requires — and decodes with any mainstream viewer/decoder.
/// GIF is deliberately absent: its 256-color palette cannot hold the 8-bit cell palette
/// plus the frame/strip colors.
///
/// PNG is written by our own <see cref="FastPng"/> (the encode hot path); all other formats
/// go through ImageSharp with lossless, speed-tuned settings.
/// </summary>
internal static class ShardImageFormat
{
    public const string Default = "png";
    public static readonly string[] Supported = ["png", "bmp", "tga", "qoi", "webp", "tiff"];

    public static string Normalize(string format)
    {
        string f = format.Trim().ToLowerInvariant();
        if (f == "tif")
            f = "tiff";
        if (!Supported.Contains(f))
            throw new ArgumentException($"Unsupported image format '{format}'. Lossless formats: {string.Join(", ", Supported)}.");
        return f;
    }

    public static string Extension(string normalizedFormat) => "." + normalizedFormat;

    /// <summary>ImageSharp encoder with lossless, throughput-oriented settings (null for PNG — FastPng handles it).</summary>
    public static IImageEncoder? CreateEncoder(string normalizedFormat) => normalizedFormat switch
    {
        "png" => null,
        "bmp" => new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Bit24, SupportTransparency = false },
        "tga" => new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Bit24, Compression = TgaCompression.RunLength },
        "qoi" => new QoiEncoder { Channels = QoiChannels.Rgb },
        "webp" => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Method = WebpEncodingMethod.Fastest, Quality = 100 },
        "tiff" => new TiffEncoder
        {
            PhotometricInterpretation = TiffPhotometricInterpretation.Rgb,
            Compression = TiffCompression.Deflate,
            CompressionLevel = SixLabors.ImageSharp.Compression.Zlib.DeflateCompressionLevel.Level1,
        },
        _ => throw new ArgumentException($"Unsupported image format '{normalizedFormat}'."),
    };

    /// <summary>
    /// ImageSharp configuration for encoding: a dedicated memory allocator with a generous pool
    /// so parallel non-PNG encodes reuse working buffers instead of churning the GC.
    /// </summary>
    public static readonly Configuration EncodeConfiguration = CreateEncodeConfiguration();

    private static Configuration CreateEncodeConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            MaximumPoolSizeMegabytes = 512,
        });
        return configuration;
    }
}
