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
    PayloadHandle Open(string filePath, long length, bool compress, AppSettings cfg, out byte flags, out byte[] sha);

    bool LooksCompressible(IPayloadSource source);
}

/// <summary>
/// Chooses how the input is exposed to the encoder:
///  - empty file → trivial in-memory source;
///  - compressible content (per a mid-file sample for large files) → deflated in memory,
///    when that actually wins;
///  - everything else → a memory-mapped source, so large incompressible files (zips, media)
///    are streamed per-chunk and never materialized as a managed array.
/// </summary>
internal sealed class PayloadPreparer : IPayloadPreparer
{
    public PayloadHandle Open(string filePath, long length, bool compress, AppSettings cfg, out byte flags, out byte[] sha)
    {
        flags = 0;
        if (length == 0)
        {
            sha = SHA256.HashData([]);
            return new PayloadHandle(new BytePayloadSource([]));
        }

        var mapped = new MappedPayloadSource(filePath);
        sha = PayloadSource.ComputeSha256(mapped);

        if (compress && LooksCompressible(mapped))
        {
            var original = new byte[length];
            mapped.Read(0, original);
            byte[] compressed = Deflate(original, cfg.PayloadCompressionLevel);
            if (compressed.Length < original.Length)
            {
                mapped.Dispose();
                flags = ShardHeader.FlagCompressed;
                return new PayloadHandle(new BytePayloadSource(compressed));
            }
        }
        return new PayloadHandle(mapped);
    }

    /// <summary>
    /// Cheap pre-check before deflating large inputs: compressing a mid-file sample at the
    /// fastest level tells us whether a full pass is worth the CPU (a .zip/.mp4 is not).
    /// </summary>
    public bool LooksCompressible(IPayloadSource source)
    {
        const int threshold = 4_000_000, sampleLen = 1_000_000;
        if (source.Length <= threshold)
            return true;
        var sample = new byte[sampleLen];
        source.Read(source.Length / 2 - sampleLen / 2, sample);
        return Deflate(sample, CompressionLevel.Fastest).Length < sampleLen * 98L / 100;
    }

    private static byte[] Deflate(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, level))
            ds.Write(data);
        return ms.ToArray();
    }
}
