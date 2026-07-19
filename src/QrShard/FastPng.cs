using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Minimal, fast PNG writer specialized for shard images: 8-bit truecolor RGB, a fixed
/// None or Up filter, zlib at fastest level, one IDAT chunk streamed straight from the
/// encoder's pixel buffer to the file — no intermediate Image object, no library overhead.
///
/// Output is fully standard PNG (readable by any viewer or decoder); this exists because a
/// general-purpose image library solves a much broader problem than "serialize pixels we
/// just rendered", and the PNG encode was the dominant cost of the whole encoder.
/// </summary>
internal sealed class FastPng(Crc crc)
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public FastPng() : this(new Crc())
    {
    }

    /// <param name="upFilter">
    /// True to apply the PNG "Up" filter to every row (ideal when rows repeat, i.e. cell size
    /// >= 2 px); false for no filtering (ideal for noise-like 1 px cells).
    /// </param>
    /// <param name="level">
    /// Deflate level for the zlib stream (configurable via appsettings.json; the caller passes
    /// Fastest for unfiltered noise content, where higher levels cannot help).
    /// </param>
    public void Write(string path, ReadOnlySpan<Rgb24> pixels, int width, int height, bool upFilter, CompressionLevel level)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        fs.Write(Signature);

        // IHDR: width, height, bit depth 8, color type 2 (truecolor), deflate, adaptive, no interlace.
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(fs, "IHDR", ihdr);

        // IDAT, streamed: length is back-patched, CRC accumulated over type + data as written.
        long lengthPosition = fs.Position;
        Span<byte> scratch = stackalloc byte[4];
        fs.Write(scratch); // length placeholder
        var idat = new ChunkBodyStream(fs, crc);
        idat.Write("IDAT"u8);

        int rowBytes = width * 3;
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(pixels[..(width * height)]);
        var filtered = new byte[1 + rowBytes];
        using (var zlib = new ZLibStream(idat, level, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = src.Slice(y * rowBytes, rowBytes);
                if (upFilter)
                {
                    // Up filter: raw - prior (prior row is all zeros for y == 0 per spec).
                    filtered[0] = 2;
                    if (y == 0)
                    {
                        row.CopyTo(filtered.AsSpan(1));
                    }
                    else
                    {
                        ReadOnlySpan<byte> prior = src.Slice((y - 1) * rowBytes, rowBytes);
                        for (int x = 0; x < rowBytes; x++)
                            filtered[1 + x] = (byte)(row[x] - prior[x]);
                    }
                }
                else
                {
                    filtered[0] = 0;
                    row.CopyTo(filtered.AsSpan(1));
                }
                zlib.Write(filtered);
            }
        }

        // Back-patch the IDAT length, then append its CRC.
        long end = fs.Position;
        BinaryPrimitives.WriteUInt32BigEndian(scratch, (uint)(end - lengthPosition - 8));
        fs.Position = lengthPosition;
        fs.Write(scratch);
        fs.Position = end;
        BinaryPrimitives.WriteUInt32BigEndian(scratch, idat.Crc);
        fs.Write(scratch);

        WriteChunk(fs, "IEND", []);
    }

    private void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(data);

        uint chunkCrc = crc.Crc32Append(crc.Crc32Begin(), header[4..]);
        chunkCrc = crc.Crc32Finish(crc.Crc32Append(chunkCrc, data));
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, chunkCrc);
        stream.Write(crcBytes);
    }

    /// <summary>Pass-through stream that accumulates the PNG chunk CRC over everything written.</summary>
    private sealed class ChunkBodyStream(Stream inner, Crc crc32) : Stream
    {
        private uint _crc = crc32.Crc32Begin();

        public uint Crc => crc32.Crc32Finish(_crc);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc = crc32.Crc32Append(_crc, buffer);
            inner.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value) => Write([value]);

        public override void Flush() => inner.Flush();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
