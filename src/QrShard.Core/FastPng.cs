using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
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
internal sealed class FastPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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
        var idat = new ChunkBodyStream(fs);
        idat.Write("IDAT"u8);

        int rowBytes = width * 3;
        ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(pixels[..(width * height)]);
        if (!upFilter)
        {
            // 1 px noise cells are incompressible by construction: deflate at any level burns
            // CPU to save ~nothing. Emit spec-legal STORED deflate blocks instead — the write
            // becomes memcpy plus an Adler-32, and every PNG decoder reads it unchanged.
            WriteStoredZlib(idat, src, rowBytes, height);
        }
        else
        {
            var filtered = new byte[1 + rowBytes];
            using var zlib = new ZLibStream(idat, level, leaveOpen: true);
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = src.Slice(y * rowBytes, rowBytes);
                // Up filter: raw - prior (prior row is all zeros for y == 0 per spec).
                filtered[0] = 2;
                if (y == 0)
                {
                    row.CopyTo(filtered.AsSpan(1));
                }
                else
                {
                    ReadOnlySpan<byte> prior = src.Slice((y - 1) * rowBytes, rowBytes);
                    Span<byte> target = filtered.AsSpan(1);
                    int x = 0;
                    // Byte subtraction has no cross-lane dependency — vectorize at the widest
                    // width the machine has.
                    for (; x <= rowBytes - Vector<byte>.Count; x += Vector<byte>.Count)
                        (new Vector<byte>(row[x..]) - new Vector<byte>(prior[x..])).CopyTo(target[x..]);
                    for (; x < rowBytes; x++)
                        target[x] = (byte)(row[x] - prior[x]);
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

    /// <summary>Hand-rolled zlib stream of STORED deflate blocks over [filter byte 0][raw row] rows.</summary>
    private static void WriteStoredZlib(Stream output, ReadOnlySpan<byte> src, int rowBytes, int height)
    {
        output.WriteByte(0x78); // zlib CMF: deflate, 32K window
        output.WriteByte(0x01); // FLG: check bits, no dict, fastest

        const int MaxBlock = 65535;
        var block = new byte[MaxBlock];
        Span<byte> blockHeader = stackalloc byte[5];
        long remaining = (long)(rowBytes + 1) * height;
        uint s1 = 1, s2 = 0; // Adler-32 state
        int row = 0, posInRow = -1; // -1 = this row's filter byte not yet emitted

        while (remaining > 0)
        {
            int fill = 0;
            while (fill < MaxBlock && row < height)
            {
                if (posInRow < 0)
                {
                    block[fill++] = 0; // filter: None
                    posInRow = 0;
                }
                else
                {
                    int n = Math.Min(MaxBlock - fill, rowBytes - posInRow);
                    src.Slice(row * rowBytes + posInRow, n).CopyTo(block.AsSpan(fill));
                    fill += n;
                    posInRow += n;
                    if (posInRow == rowBytes)
                    {
                        row++;
                        posInRow = -1;
                    }
                }
            }

            bool final = remaining == fill;
            blockHeader[0] = (byte)(final ? 1 : 0); // BFINAL, BTYPE=00 (stored)
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader[1..], (ushort)fill);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader[3..], (ushort)~fill);
            output.Write(blockHeader);
            output.Write(block, 0, fill);
            UpdateAdler(ref s1, ref s2, block.AsSpan(0, fill));
            remaining -= fill;
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, (s2 << 16) | s1);
        output.Write(adler);
    }

    private static void UpdateAdler(ref uint s1, ref uint s2, ReadOnlySpan<byte> data)
    {
        const int NMax = 5552; // max bytes before the sums can overflow uint
        int i = 0;
        while (i < data.Length)
        {
            int end = Math.Min(i + NMax, data.Length);
            for (; i < end; i++)
            {
                s1 += data[i];
                s2 += s1;
            }
            s1 %= 65521;
            s2 %= 65521;
        }
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(data);

        var chunkCrc = new System.IO.Hashing.Crc32();
        chunkCrc.Append(header[4..]);
        chunkCrc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, chunkCrc.GetCurrentHashAsUInt32());
        stream.Write(crcBytes);
    }

    /// <summary>Pass-through stream that accumulates the PNG chunk CRC over everything written.</summary>
    private sealed class ChunkBodyStream(Stream inner) : Stream
    {
        private readonly System.IO.Hashing.Crc32 _crc = new();

        public uint Crc => _crc.GetCurrentHashAsUInt32();

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _crc.Append(buffer);
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
