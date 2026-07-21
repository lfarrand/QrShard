using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Minimal, fast PNG reader — the decode-side counterpart of <see cref="FastPng"/>. Handles the
/// common truecolor subset: 8-bit RGB or RGBA, non-interlaced, all five standard row filters.
/// That covers both our own shard files and typical screenshot-tool output, where the
/// general-purpose decoder's flexibility was the decode-side bottleneck. Anything outside the
/// subset (palette, 16-bit, grayscale, interlaced) returns false and the caller falls back to
/// ImageSharp.
/// </summary>
internal sealed class FastPngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Pixels land in the scratch's pooled buffer, like the ImageSharp path.</summary>
    public bool TryRead(string path, DecodeScratch scratch, out Bitmap bitmap)
    {
        bitmap = null!;
        try
        {
            return TryReadCore(path, scratch, ref bitmap);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return false; // malformed or truncated — let ImageSharp produce the real error
        }
    }

    private static bool TryReadCore(string path, DecodeScratch scratch, ref Bitmap bitmap)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> sig = stackalloc byte[8];
        if (fs.Read(sig) != 8 || !sig.SequenceEqual(Signature))
            return false;

        const uint Ihdr = 0x49484452, Idat = 0x49444154, Iend = 0x49454E44;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[13]; // outside the loop: stackalloc in a loop never frees (CA2014)
        int width = 0, height = 0, bytesPerPixel = 0;
        bool haveHeader = false;
        var idatRanges = new List<(long Offset, int Length)>();

        while (true)
        {
            if (fs.Read(chunkHeader) != 8)
                return false;
            int length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[4..]);
            if (length < 0)
                return false;

            if (type == Ihdr)
            {
                if (length != 13 || fs.Read(ihdr) != 13)
                    return false;
                width = BinaryPrimitives.ReadInt32BigEndian(ihdr);
                height = BinaryPrimitives.ReadInt32BigEndian(ihdr[4..]);
                int bitDepth = ihdr[8], colorType = ihdr[9], compression = ihdr[10], filter = ihdr[11], interlace = ihdr[12];
                if (bitDepth != 8 || (colorType != 2 && colorType != 6) || compression != 0 || filter != 0 || interlace != 0)
                    return false;
                if (width < 1 || height < 1 || (long)width * height > 500_000_000)
                    return false;
                bytesPerPixel = colorType == 2 ? 3 : 4;
                haveHeader = true;
                fs.Seek(4, SeekOrigin.Current); // chunk CRC
            }
            else if (type == Idat)
            {
                idatRanges.Add((fs.Position, length));
                fs.Seek(length + 4, SeekOrigin.Current);
            }
            else if (type == Iend)
            {
                break;
            }
            else
            {
                fs.Seek(length + 4, SeekOrigin.Current); // ancillary chunk — irrelevant to pixels
            }
        }
        if (!haveHeader || idatRanges.Count == 0)
            return false;

        var px = scratch.Pixels(width * height);

        // Peek the first deflate block type: our own max-density shards are written as STORED
        // blocks, which a direct copier serves at memcpy speed — the zlib state machine only
        // runs for genuinely compressed streams.
        Span<byte> head = stackalloc byte[3]; // 2-byte zlib header + first block-type byte
        if (!ReadFully(new ChunkRangeStream(fs, idatRanges), head))
            return false;
        bool stored = (head[2] & 0x06) == 0; // BTYPE bits (1-2, LSB-first) == 00
        using Stream inflate = stored
            ? new StoredInflateStream(new ChunkRangeStream(fs, idatRanges))
            : new ZLibStream(new ChunkRangeStream(fs, idatRanges), CompressionMode.Decompress);

        int rowBytes = width * bytesPerPixel;
        var row = new byte[rowBytes];
        var prior = new byte[rowBytes]; // all-zero for y == 0, per spec
        var filterByte = new byte[1];
        for (int y = 0; y < height; y++)
        {
            inflate.ReadExactly(filterByte);
            inflate.ReadExactly(row);
            Unfilter(filterByte[0], row, prior, bytesPerPixel);
            if (bytesPerPixel == 3)
            {
                MemoryMarshal.Cast<byte, Rgb24>(row.AsSpan()).CopyTo(px.AsSpan(y * width, width));
            }
            else
            {
                for (int x = 0; x < width; x++)
                    px[y * width + x] = new Rgb24(row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
            }
            (row, prior) = (prior, row);
        }

        bitmap = new Bitmap(px, width, height);
        return true;
    }

    private static void Unfilter(byte filter, byte[] row, byte[] prior, int bpp)
    {
        int n = row.Length;
        switch (filter)
        {
            case 0: // None
                break;
            case 1: // Sub
                for (int i = bpp; i < n; i++)
                    row[i] += row[i - bpp];
                break;
            case 2: // Up
                for (int i = 0; i < n; i++)
                    row[i] += prior[i];
                break;
            case 3: // Average
                for (int i = 0; i < bpp; i++)
                    row[i] += (byte)(prior[i] >> 1);
                for (int i = bpp; i < n; i++)
                    row[i] += (byte)((row[i - bpp] + prior[i]) >> 1);
                break;
            case 4: // Paeth (first pixel: a = c = 0, predictor degenerates to b)
                for (int i = 0; i < bpp; i++)
                    row[i] += prior[i];
                for (int i = bpp; i < n; i++)
                    row[i] += Paeth(row[i - bpp], prior[i], prior[i - bpp]);
                break;
            default:
                throw new InvalidDataException("Unknown PNG row filter.");
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = stream.Read(buffer[offset..]);
            if (n == 0)
                return false;
            offset += n;
        }
        return true;
    }

    /// <summary>
    /// Serves a zlib stream consisting purely of STORED deflate blocks by direct copy — the
    /// decode-side twin of the writer's stored-block fast path. A non-stored block mid-stream
    /// throws InvalidDataException, which the outer TryRead turns into an ImageSharp fallback.
    /// </summary>
    private sealed class StoredInflateStream : Stream
    {
        private readonly Stream _inner;
        private int _remainingInBlock;
        private bool _finalSeen;

        public StoredInflateStream(Stream inner)
        {
            _inner = inner;
            Span<byte> zlibHeader = stackalloc byte[2];
            if (!ReadFully(inner, zlibHeader))
                throw new InvalidDataException("Truncated zlib stream.");
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Span<byte> header = stackalloc byte[5]; // outside the loop: stackalloc in a loop never frees (CA2014)
            while (true)
            {
                if (_remainingInBlock > 0)
                {
                    int n = _inner.Read(buffer[..Math.Min(buffer.Length, _remainingInBlock)]);
                    _remainingInBlock -= n;
                    return n;
                }
                if (_finalSeen)
                    return 0;

                if (!ReadFully(_inner, header))
                    return 0; // truncated — surfaces as EndOfStreamException at the row reader
                if ((header[0] & 0x06) != 0)
                    throw new InvalidDataException("Mixed stored/compressed deflate blocks."); // fall back
                _finalSeen = (header[0] & 1) != 0;
                int length = BinaryPrimitives.ReadUInt16LittleEndian(header[1..]);
                if (length != (ushort)~BinaryPrimitives.ReadUInt16LittleEndian(header[3..]))
                    throw new InvalidDataException("Corrupt stored-block length.");
                _remainingInBlock = length;
                if (length == 0 && _finalSeen)
                    return 0;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reads the concatenated IDAT payloads as one stream without copying them out first.</summary>
    private sealed class ChunkRangeStream(FileStream fs, List<(long Offset, int Length)> ranges) : Stream
    {
        private int _index;
        private int _posInChunk;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_index < ranges.Count)
            {
                var (chunkOffset, chunkLength) = ranges[_index];
                int remaining = chunkLength - _posInChunk;
                if (remaining == 0)
                {
                    _index++;
                    _posInChunk = 0;
                    continue;
                }
                fs.Position = chunkOffset + _posInChunk;
                int n = fs.Read(buffer[..Math.Min(buffer.Length, remaining)]);
                if (n == 0)
                    return 0;
                _posInChunk += n;
                return n;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
