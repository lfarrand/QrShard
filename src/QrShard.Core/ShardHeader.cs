using System.Text;

namespace QrShard;

/// <summary>
/// Per-image header carried at the front of every shard's data stream.
/// Every field needed for reassembly is repeated in every image, so any subset of
/// captures can be validated independently and missing shards identified precisely.
///
/// Cross-shard parity (see <see cref="CrossShardFec"/>) adds parity images whose header sets
/// <see cref="FlagParity"/>. For a data image, <see cref="Index"/> is 0..Count-1. For a parity
/// image, it is the global parity ordinal 0..(stripes*StripeParity-1). Stripe geometry
/// (<see cref="StripeData"/>, <see cref="StripeParity"/>) is repeated in every image so any
/// survivor reveals the whole recovery layout.
/// </summary>
internal sealed class ShardHeader
{
    public static readonly byte[] Magic = "QRS1"u8.ToArray();
    public const byte FlagCompressed = 0x01;  // payload is compressed (deflate unless FlagBrotli)
    public const byte FlagParity = 0x02;      // this image is cross-shard parity, not data
    public const byte FlagBrotli = 0x04;      // compression algorithm is Brotli (with FlagCompressed)
    public const byte FlagEncrypted = 0x08;   // payload is AES-256-GCM encrypted (password required)
    public const byte FlagArchive = 0x10;     // payload is a tar archive of a folder
    public const byte FlagFountain = 0x20;    // parity images are random-linear fountain frames
    public const byte KnownFlags = 0x3F;      // every flag this build understands
    private const byte HeaderVersion = 2;

    public required ulong FileId { get; init; }
    public required int Index { get; init; }
    public required int Count { get; init; }             // number of DATA images
    public required int PayloadLength { get; init; }
    public required uint PayloadCrc32 { get; init; }
    public required long TotalLength { get; init; }      // length of the (possibly compressed) stream that was split
    public required long OriginalLength { get; init; }   // length of the original file
    public required byte Flags { get; init; }
    public required byte[] Sha256 { get; init; }         // hash of the original file
    public required string FileName { get; init; }
    public int StripeData { get; init; }                 // data images per stripe (0 = no cross-shard parity)
    public int StripeParity { get; init; }               // parity images per stripe

    public bool IsParity => (Flags & FlagParity) != 0;

    public static int Size(string fileName) => 92 + Encoding.UTF8.GetByteCount(fileName);

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(HeaderVersion);
        w.Write(Flags);
        w.Write(FileId);
        w.Write(Index);
        w.Write(Count);
        w.Write(PayloadLength);
        w.Write(PayloadCrc32);
        w.Write(TotalLength);
        w.Write(OriginalLength);
        w.Write(StripeData);
        w.Write(StripeParity);
        w.Write(Sha256);
        byte[] name = Encoding.UTF8.GetBytes(FileName);
        w.Write((ushort)name.Length);
        w.Write(name);
        w.Flush();
        byte[] body = ms.ToArray();
        w.Write(new Crc().Crc32(body));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Parses and validates a header from the front of a decoded stream. Returns null if invalid.</summary>
    public static ShardHeader? Deserialize(byte[] stream, out int headerLength)
    {
        headerLength = 0;
        if (stream.Length < 92 || !stream.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        try
        {
            using var ms = new MemoryStream(stream);
            using var r = new BinaryReader(ms);
            r.ReadBytes(4);
            byte version = r.ReadByte();
            if (version != HeaderVersion)
                return null;
            byte flags = r.ReadByte();
            ulong fileId = r.ReadUInt64();
            int index = r.ReadInt32();
            int count = r.ReadInt32();
            int payloadLength = r.ReadInt32();
            uint payloadCrc = r.ReadUInt32();
            long totalLength = r.ReadInt64();
            long originalLength = r.ReadInt64();
            int stripeData = r.ReadInt32();
            int stripeParity = r.ReadInt32();
            byte[] sha = r.ReadBytes(32);
            int nameLen = r.ReadUInt16();
            if (nameLen > 4096 || ms.Position + nameLen + 4 > stream.Length)
                return null;
            string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
            int bodyLen = (int)ms.Position;
            uint headerCrc = r.ReadUInt32();
            if (headerCrc != new Crc().Crc32(stream.AsSpan(0, bodyLen)))
                return null;

            bool isParity = (flags & FlagParity) != 0;
            if (count < 1 || payloadLength < 0 || stripeData < 0 || stripeParity < 0)
                return null;
            if (index < 0)
                return null;
            if (!isParity && index >= count)
                return null; // data ordinal must fall within the data range
            // Cross-shard coding needs a positive stripe width: stripeData is a divisor and an
            // array dimension in the reassembler and the completeness check. Reject the crafted
            // combination here — the single choke point every header passes through — so no
            // downstream math ever sees stripeData == 0 with parity present.
            if (stripeParity > 0 && stripeData < 1)
                return null;

            headerLength = bodyLen + 4;
            return new ShardHeader
            {
                FileId = fileId,
                Index = index,
                Count = count,
                PayloadLength = payloadLength,
                PayloadCrc32 = payloadCrc,
                TotalLength = totalLength,
                OriginalLength = originalLength,
                Flags = flags,
                Sha256 = sha,
                FileName = name,
                StripeData = stripeData,
                StripeParity = stripeParity,
            };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
