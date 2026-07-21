namespace QrShard;

/// <summary>
/// CRC implementations used for image payload (CRC-32/IEEE) and metadata strip (CRC-16/CCITT).
/// CRC-32 delegates to System.IO.Hashing's implementation of the SAME polynomial
/// (0xEDB88320 reflected, init/xorout 0xFFFFFFFF) — wire format unchanged, but bulk hashing
/// uses carryless-multiply folding instead of a byte-at-a-time table walk, roughly an order of
/// magnitude faster over the megabytes each transfer CRCs. CRC-16 only ever sees the 14-byte
/// metadata payload; scalar is fine.
/// </summary>
internal sealed class Crc
{
    public uint Crc32(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);

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
