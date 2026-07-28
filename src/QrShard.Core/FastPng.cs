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

    /// <summary>
    /// Groups per SIMD fold. The vs2 lane accumulator is 16-bit and reaches 255*m*(m-1)/2 over a
    /// group of m blocks, so 23 is the largest group that cannot overflow it (23 → 64,515;
    /// 24 → 70,380 > ushort.MaxValue).
    /// </summary>
    private const int AdlerGroupBlocks = 23;

    /// <summary>
    /// Positional weight (V - k) for byte k of a block, pre-split across the four Vector&lt;uint&gt;
    /// halves that Widen produces from one Vector&lt;byte&gt;. Widen's element order is defined
    /// (low half first), so lane p of quarter q always carries byte q*Count + p.
    /// </summary>
    private static readonly Vector<uint>[] AdlerWeights = BuildAdlerWeights();

    private static Vector<uint>[] BuildAdlerWeights()
    {
        int v = Vector<byte>.Count;
        var weights = new uint[v];
        for (int k = 0; k < v; k++)
            weights[k] = (uint)(v - k);
        int quarter = Vector<uint>.Count; // == v / 4
        var result = new Vector<uint>[4];
        for (int q = 0; q < 4; q++)
            result[q] = new Vector<uint>(weights, q * quarter);
        return result;
    }

    /// <summary>
    /// Folds <paramref name="data"/> into the running Adler-32 state.
    /// </summary>
    /// <remarks>
    /// Written byte at a time this is a serial dependency chain (s2 += s1 += b), which is why the
    /// scalar form runs at a fraction of the memory bandwidth a hardware CRC-32 achieves over the
    /// same bytes. The chain breaks if the positional weight each byte carries into s2 is applied
    /// once per group rather than once per byte: for a group of m blocks of V bytes, with
    /// vs1[k] = sum_t b[t][k] and vs2[k] = sum_t (m-1-t)*b[t][k] (vs2 accumulating vs1 as of the
    /// PREVIOUS block), the byte i = t*V + k contributes (m*V - i) = (m-1-t)*V + (V-k) to s2, so
    ///     s2 += m*V*s1 + V*sum(vs2) + sum_k (V-k)*vs1[k]
    ///     s1 += sum(vs1)
    /// is the same recurrence, regrouped. Every lane then accumulates independently.
    /// </remarks>
    internal static void UpdateAdler(ref uint s1, ref uint s2, ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int v = Vector<byte>.Count;
            int groupBytes = AdlerGroupBlocks * v;
            var weights = AdlerWeights;
            while (data.Length - i >= groupBytes)
            {
                Vector<ushort> vs1Lo = default, vs1Hi = default, vs2Lo = default, vs2Hi = default;
                for (int t = 0; t < AdlerGroupBlocks; t++, i += v)
                {
                    // vs2 must see vs1 as of the previous block — that lag IS the (m-1-t) weight.
                    vs2Lo += vs1Lo;
                    vs2Hi += vs1Hi;
                    Vector.Widen(new Vector<byte>(data.Slice(i, v)), out var lo, out var hi);
                    vs1Lo += lo;
                    vs1Hi += hi;
                }

                // 32-bit lanes for the reductions: the totals exceed 16 bits even though no
                // individual lane accumulator did.
                Vector.Widen(vs1Lo, out var a0, out var a1);
                Vector.Widen(vs1Hi, out var a2, out var a3);
                Vector.Widen(vs2Lo, out var c0, out var c1);
                Vector.Widen(vs2Hi, out var c2, out var c3);

                uint sum1 = Vector.Sum(a0 + a1 + a2 + a3);
                uint weighted = Vector.Sum(a0 * weights[0] + a1 * weights[1] + a2 * weights[2] + a3 * weights[3]);
                uint sum2 = Vector.Sum(c0 + c1 + c2 + c3);

                // Bounded by 65520 + 1472*65520 + 64*4.13e6 + 2.4e7 < 2^32 at the widest V (64).
                s2 += (uint)groupBytes * s1 + (uint)v * sum2 + weighted;
                s1 += sum1;
                s1 %= 65521;
                s2 %= 65521;
            }
        }

        // Scalar remainder — and the whole input where SIMD is unavailable.
        const int NMax = 5552; // max bytes before the sums can overflow uint
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
