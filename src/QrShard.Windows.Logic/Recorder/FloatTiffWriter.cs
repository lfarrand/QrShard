using System.IO.Compression;
using System.Runtime.InteropServices;

namespace QrShard.Recorder;

/// <summary>
/// Writes 32-bit float RGBA (scRGB) frames as classic little-endian TIFF with IEEE
/// float SampleFormat and Adobe Deflate strips. WPF's TiffBitmapEncoder cannot hold
/// float pixels.
/// </summary>
internal static class FloatTiffWriter
{
    private const int RowsPerStrip = 64;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    public static void Write(Stream output, float[] pixels, int width, int height)
    {
        int rowBytes = width * 16;
        int stripCount = (height + RowsPerStrip - 1) / RowsPerStrip;

        var strips = new byte[stripCount][];
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan(0, width * height * 4));
        for (int s = 0; s < stripCount; s++)
        {
            int rows = Math.Min(RowsPerStrip, height - s * RowsPerStrip);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(bytes.Slice(s * RowsPerStrip * rowBytes, rows * rowBytes));
            strips[s] = compressed.ToArray();
        }

        const int entryCount = 11;
        const int headerSize = 8;
        const int ifdSize = 2 + entryCount * 12 + 4;
        uint bitsOffset = headerSize + ifdSize;
        uint formatOffset = bitsOffset + 8;
        uint stripOffsetsOffset = formatOffset + 8;
        uint stripCountsOffset = stripOffsetsOffset + (uint)(4 * stripCount);
        uint dataStart = stripCountsOffset + (uint)(4 * stripCount);

        var writer = new BinaryWriter(output);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)headerSize);

        writer.Write((ushort)entryCount);
        WriteEntry(writer, 256, TypeLong, 1, (uint)width);
        WriteEntry(writer, 257, TypeLong, 1, (uint)height);
        WriteEntry(writer, 258, TypeShort, 4, bitsOffset);
        WriteEntry(writer, 259, TypeShort, 1, 8);
        WriteEntry(writer, 262, TypeShort, 1, 2);
        WriteEntry(writer, 273, TypeLong, (uint)stripCount, stripOffsetsOffset);
        WriteEntry(writer, 277, TypeShort, 1, 4);
        WriteEntry(writer, 278, TypeLong, 1, RowsPerStrip);
        WriteEntry(writer, 279, TypeLong, (uint)stripCount, stripCountsOffset);
        WriteEntry(writer, 338, TypeShort, 1, 2);
        WriteEntry(writer, 339, TypeShort, 4, formatOffset);
        writer.Write((uint)0);

        for (int i = 0; i < 4; i++) writer.Write((ushort)32);
        for (int i = 0; i < 4; i++) writer.Write((ushort)3);

        uint offset = dataStart;
        foreach (byte[] strip in strips)
        {
            writer.Write(offset);
            offset += (uint)strip.Length;
        }
        foreach (byte[] strip in strips)
            writer.Write((uint)strip.Length);
        foreach (byte[] strip in strips)
            writer.Write(strip);
        writer.Flush();
    }

    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        if (type == TypeShort && count == 1)
        {
            writer.Write((ushort)value);
            writer.Write((ushort)0);
        }
        else
            writer.Write(value);
    }
}
