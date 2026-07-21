using System.Buffers.Binary;

namespace QrShard;

/// <summary>Persists decoded shards between decode runs.</summary>
internal interface ISessionStore
{
    List<DecodedShard> Load(string path);

    void Save(string path, IReadOnlyCollection<DecodedShard> shards);
}

/// <summary>
/// On-disk session file so capture can happen over multiple sittings: every successfully
/// decoded shard is persisted, and the next run resumes from the union instead of requiring
/// all images at once. Format: "QRSS" magic, version, count, then per shard the serialized
/// (CRC-guarded) header followed by the payload. Corrupt or unreadable entries are skipped —
/// the worst case is recapturing an image, never a bad byte in the output (payloads are still
/// CRC-checked here and the assembled file SHA-256-verified).
/// </summary>
internal sealed class SessionStore(Crc crc) : ISessionStore
{
    private static readonly byte[] Magic = "QRSS"u8.ToArray();
    private const byte Version = 1;

    public SessionStore() : this(new Crc())
    {
    }

    public List<DecodedShard> Load(string path)
    {
        var shards = new List<DecodedShard>();
        if (!File.Exists(path))
            return shards;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadByte() != Version)
                return shards;
            int count = r.ReadInt32();
            for (int i = 0; i < count && i < 1_000_000; i++)
            {
                int headerLen = r.ReadInt32();
                if (headerLen is < 0 or > 1_000_000)
                    return shards;
                byte[] headerBytes = r.ReadBytes(headerLen);
                int payloadLen = r.ReadInt32();
                if (payloadLen is < 0 or > int.MaxValue / 2)
                    return shards;
                byte[] payload = r.ReadBytes(payloadLen);
                if (headerBytes.Length != headerLen || payload.Length != payloadLen)
                    return shards;

                var header = ShardHeader.Deserialize(headerBytes, out _);
                if (header is null || payload.Length != header.PayloadLength || crc.Crc32(payload) != header.PayloadCrc32)
                    continue; // damaged entry — drop it, keep the rest
                shards.Add(new DecodedShard(header, payload, path, 0, 0));
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            // Truncated session — keep whatever parsed cleanly.
        }
        return shards;
    }

    public void Save(string path, IReadOnlyCollection<DecodedShard> shards)
    {
        string temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(shards.Count);
            foreach (var shard in shards)
            {
                byte[] headerBytes = shard.Header.Serialize();
                w.Write(headerBytes.Length);
                w.Write(headerBytes);
                w.Write(shard.Payload.Length);
                w.Write(shard.Payload);
            }
        }
        File.Move(temp, path, overwrite: true); // atomic-ish: never leave a half-written session
    }
}
