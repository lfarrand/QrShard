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

internal static class BitStream
{
    /// <summary>Reads <paramref name="bits"/> bits MSB-first starting at an absolute bit offset; missing bytes read as 0.</summary>
    public static int ReadCell(byte[] data, long bitOffset, int bits)
    {
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
    public static void WriteCell(byte[] data, long bitOffset, int bits, int value)
    {
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
}
