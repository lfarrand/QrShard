namespace QrShard;

/// <summary>CRC implementations used for image payload (CRC-32/IEEE) and metadata strip (CRC-16/CCITT).</summary>
internal sealed class Crc
{
    private static readonly uint[] Table32 = BuildTable32();

    private static uint[] BuildTable32()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public uint Crc32(ReadOnlySpan<byte> data) => Crc32Finish(Crc32Append(Crc32Begin(), data));

    // Incremental CRC-32 (for streamed writers, e.g. the PNG chunk CRC).
    public uint Crc32Begin() => 0xFFFFFFFFu;

    public uint Crc32Append(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            state = Table32[(state ^ b) & 0xFF] ^ (state >> 8);
        return state;
    }

    public uint Crc32Finish(uint state) => state ^ 0xFFFFFFFFu;

    public ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int k = 0; k < 8; k++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
