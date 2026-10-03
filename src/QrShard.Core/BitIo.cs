namespace QrShard;

/// <summary>MSB-first bit writer.</summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitCount;

    public void Write(uint value, int bits)
    {
        for (int k = bits - 1; k >= 0; k--)
        {
            if ((_bitCount & 7) == 0)
                _bytes.Add(0);
            if (((value >> k) & 1) != 0)
                _bytes[^1] |= (byte)(0x80 >> (_bitCount & 7));
            _bitCount++;
        }
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>MSB-first bit reader.</summary>
internal sealed class BitReader(byte[] data)
{
    private long _pos;

    public uint Read(int bits)
    {
        uint v = 0;
        for (int k = 0; k < bits; k++)
        {
            long byteIdx = _pos >> 3;
            int bit = byteIdx < data.Length ? (data[byteIdx] >> (7 - (int)(_pos & 7))) & 1 : 0;
            v = (v << 1) | (uint)bit;
            _pos++;
        }
        return v;
    }
}

internal sealed class BitStream
{
    /// <summary>Reads <paramref name="bits"/> bits MSB-first starting at an absolute bit offset; missing bytes read as 0.</summary>
    public int ReadCell(byte[] data, long bitOffset, int bits)
    {
        if ((uint)bits <= 12)
        {
            // A run of <= 12 bits spans at most three bytes. One 24-bit window covers a 12-bit
            // cell that starts on the last bit of a byte, and a missing byte still reads as 0.
            long byteIdx = bitOffset >> 3;
            if (byteIdx >= data.Length)
                return 0;
            int bitInByte = (int)(bitOffset & 7);
            int b0 = data[byteIdx];
            int b1 = byteIdx + 1 < data.Length ? data[byteIdx + 1] : 0;
            int b2 = byteIdx + 2 < data.Length ? data[byteIdx + 2] : 0;
            int window = (b0 << 16) | (b1 << 8) | b2;
            return (window >> (24 - bitInByte - bits)) & ((1 << bits) - 1);
        }

        int v = 0;
        for (int k = 0; k < bits; k++)
        {
            long bo = bitOffset + k;
            long byteIdx = bo >> 3;
            int bit = byteIdx < data.Length ? (data[byteIdx] >> (7 - (int)(bo & 7))) & 1 : 0;
            v = (v << 1) | bit;
        }
        return v;
    }

    /// <summary>Writes <paramref name="bits"/> bits MSB-first at an absolute bit offset; bits past the buffer are dropped.</summary>
    public void WriteCell(byte[] data, long bitOffset, int bits, int value)
    {
        if ((uint)bits <= 12)
        {
            long byteIdx = bitOffset >> 3;
            if (byteIdx >= data.Length)
                return;
            int bitInByte = (int)(bitOffset & 7);
            int shifted = (value & ((1 << bits) - 1)) << (24 - bitInByte - bits);
            data[byteIdx] |= (byte)(shifted >> 16);
            if (byteIdx + 1 < data.Length)
                data[byteIdx + 1] |= (byte)(shifted >> 8);
            if (byteIdx + 2 < data.Length)
                data[byteIdx + 2] |= (byte)shifted;
            return;
        }

        for (int k = 0; k < bits; k++)
        {
            long bo = bitOffset + k;
            long byteIdx = bo >> 3;
            if (byteIdx >= data.Length)
                return;
            if (((value >> (bits - 1 - k)) & 1) != 0)
                data[byteIdx] |= (byte)(0x80 >> (int)(bo & 7));
        }
    }

    /// <summary>
    /// Clears <paramref name="bits"/> bits MSB-first at an absolute bit offset. The runner-up
    /// writer copies a finished primary byte and then replaces one cell, and <see cref="WriteCell"/>
    /// ORs, so the cell's old bits have to be removed first.
    /// </summary>
    public void ClearCell(byte[] data, long bitOffset, int bits)
    {
        if ((uint)bits <= 12)
        {
            long byteIdx = bitOffset >> 3;
            if (byteIdx >= data.Length)
                return;
            int bitInByte = (int)(bitOffset & 7);
            int mask = ((1 << bits) - 1) << (24 - bitInByte - bits);
            data[byteIdx] &= (byte)~(mask >> 16);
            if (byteIdx + 1 < data.Length)
                data[byteIdx + 1] &= (byte)~(mask >> 8);
            if (byteIdx + 2 < data.Length)
                data[byteIdx + 2] &= (byte)~mask;
            return;
        }

        for (int k = 0; k < bits; k++)
        {
            long bo = bitOffset + k;
            long byteIdx = bo >> 3;
            if (byteIdx >= data.Length)
                return;
            data[byteIdx] &= (byte)~(0x80 >> (int)(bo & 7));
        }
    }
}

/// <summary>
/// Packs a row of cell indices into whole bytes for the depths the sampler actually uses.
/// The layout is the same MSB-first stream <see cref="BitStream"/> writes one cell at a time.
/// </summary>
internal static class RowPacker
{
    public static bool Supports(int bits) => bits is 4 or 6 or 8 or 10 or 11;

    /// <summary>
    /// Writes <paramref name="cells"/> at <paramref name="bitOffset"/>. Groups that start on a
    /// byte and fill whole bytes are stored directly; a short tail, or a cursor that is still
    /// mid-byte, falls back to <see cref="BitStream.WriteCell"/>.
    /// </summary>
    public static void Write(byte[] dest, long bitOffset, int bits, ReadOnlySpan<int> cells, BitStream stream)
    {
        int group = GroupCells(bits);
        int i = 0;
        long cursor = bitOffset;
        while (i < cells.Length)
        {
            if (group > 0 && (cursor & 7) == 0 && i + group <= cells.Length)
            {
                WriteGroup(dest, (int)(cursor >> 3), bits, cells.Slice(i, group));
                i += group;
                cursor += (long)group * bits;
                continue;
            }

            stream.WriteCell(dest, cursor, bits, cells[i]);
            cursor += bits;
            i++;
        }
    }

    /// <summary>Cells that fill an integral number of bytes at this depth. Zero when the depth is unpacked.</summary>
    private static int GroupCells(int bits) => bits switch
    {
        4 => 2,   // one byte
        6 => 4,   // three bytes
        8 => 1,   // one byte
        10 => 4,  // five bytes
        11 => 8,  // eleven bytes
        _ => 0,
    };

    private static void WriteGroup(byte[] dest, int at, int bits, ReadOnlySpan<int> cells)
    {
        switch (bits)
        {
            case 4:
                dest[at] = (byte)(((cells[0] & 0xF) << 4) | (cells[1] & 0xF));
                return;
            case 6:
            {
                uint packed =
                    ((uint)(cells[0] & 0x3F) << 18) |
                    ((uint)(cells[1] & 0x3F) << 12) |
                    ((uint)(cells[2] & 0x3F) << 6) |
                    (uint)(cells[3] & 0x3F);
                dest[at] = (byte)(packed >> 16);
                dest[at + 1] = (byte)(packed >> 8);
                dest[at + 2] = (byte)packed;
                return;
            }
            case 8:
                dest[at] = (byte)cells[0];
                return;
            case 10:
            {
                ulong c0 = (uint)(cells[0] & 0x3FF);
                ulong c1 = (uint)(cells[1] & 0x3FF);
                ulong c2 = (uint)(cells[2] & 0x3FF);
                ulong c3 = (uint)(cells[3] & 0x3FF);
                ulong packed = (c0 << 30) | (c1 << 20) | (c2 << 10) | c3;
                dest[at] = (byte)(packed >> 32);
                dest[at + 1] = (byte)(packed >> 24);
                dest[at + 2] = (byte)(packed >> 16);
                dest[at + 3] = (byte)(packed >> 8);
                dest[at + 4] = (byte)packed;
                return;
            }
            case 11:
            {
                ulong acc = 0;
                int n = 0;
                int o = at;
                for (int i = 0; i < 8; i++)
                {
                    acc = (acc << 11) | (uint)(cells[i] & 0x7FF);
                    n += 11;
                    while (n >= 8)
                    {
                        n -= 8;
                        dest[o++] = (byte)(acc >> n);
                    }
                    acc = n == 0 ? 0 : acc & ((1UL << n) - 1);
                }
                return;
            }
        }
    }
}
