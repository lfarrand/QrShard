using System.IO.Compression;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>Owns the chosen payload source for one encode; disposing releases any file mapping.</summary>
internal sealed class PayloadHandle(IPayloadSource source) : IDisposable
{
    public IPayloadSource Source => source;

    public void Dispose() => source.Dispose();
}

/// <summary>Chooses how the input file is exposed to the encoder and computes its digest.</summary>
internal interface IPayloadPreparer
{
    PayloadHandle Open(string filePath, long length, bool compress, string? password, AppSettings cfg,
        out byte flags, out byte[] sha);

    bool LooksCompressible(IPayloadSource source);
}

/// <summary>
/// Chooses how the input is exposed to the encoder:
///  - empty file → trivial in-memory source;
///  - compressible content (per a mid-file sample for large files) → Brotli-compressed in
///    memory, when that actually wins;
///  - everything else → a memory-mapped source, so large incompressible files (zips, media)
///    are streamed per-chunk and never materialized as a managed array;
///  - a password additionally AES-256-GCM encrypts the (possibly compressed) payload — this
///    path materializes the payload in memory, because GCM authenticates the whole message.
/// The header SHA-256 is always the hash of the ORIGINAL file, so verification happens after
/// decrypt + decompress on the receiving side.
/// </summary>
internal sealed class PayloadPreparer(PayloadCipher cipher) : IPayloadPreparer
{
    public PayloadPreparer() : this(new PayloadCipher())
    {
    }

    public PayloadHandle Open(string filePath, long length, bool compress, string? password, AppSettings cfg,
        out byte flags, out byte[] sha)
    {
        flags = 0;
        if (length == 0)
        {
            sha = SHA256.HashData([]);
            byte[] empty = [];
            if (password is not null)
            {
                empty = cipher.Encrypt(empty, password);
                flags |= ShardHeader.FlagEncrypted;
            }
            return new PayloadHandle(new BytePayloadSource(empty));
        }

        var mapped = new MappedPayloadSource(filePath);
        sha = PayloadSource.ComputeSha256(mapped);

        byte[]? material = null;
        if (compress && LooksCompressible(mapped))
        {
            var original = new byte[length];
            mapped.Read(0, original);
            byte[] compressed = Compress(original, cfg.PayloadCompressionLevel);
            if (compressed.Length < original.Length)
            {
                material = compressed;
                flags |= ShardHeader.FlagCompressed | ShardHeader.FlagBrotli;
            }
        }

        if (password is not null)
        {
            if (material is null)
            {
                material = new byte[length];
                mapped.Read(0, material);
            }
            material = cipher.Encrypt(material, password);
            flags |= ShardHeader.FlagEncrypted;
        }

        if (material is not null)
        {
            mapped.Dispose();
            return new PayloadHandle(new BytePayloadSource(material));
        }
        return new PayloadHandle(mapped);
    }

    /// <summary>
    /// Cheap pre-check before compressing large inputs: deflating a mid-file sample at the
    /// fastest level tells us whether a full pass is worth the CPU (a .zip/.mp4 is not).
    /// </summary>
    public bool LooksCompressible(IPayloadSource source)
    {
        const int threshold = 4_000_000, sampleLen = 1_000_000;
        if (source.Length <= threshold)
            return true;
        var sample = new byte[sampleLen];
        source.Read(source.Length / 2 - sampleLen / 2, sample);
        using var ms = new MemoryStream();
        using (var probe = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            probe.Write(sample);
        return ms.Length < sampleLen * 98L / 100;
    }

    private static byte[] Compress(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, level))
            brotli.Write(data);
        return ms.ToArray();
    }
}
