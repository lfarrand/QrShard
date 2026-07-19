using System.IO.Compression;
using System.Security.Cryptography;

namespace QrShard;

/// <summary>Reassembles decoded shards into output files (contiguous path + shared plumbing).</summary>
internal sealed class ShardAssembler(IParityReassembler parityReassembler) : IShardAssembler
{
    /// <summary>Reassembles already-decoded shards into output file(s). Shared by folder and video decoding.</summary>
    public List<RestoredFile> Assemble(List<DecodedShard> shards, string? outputPath, Action<string> log)
    {
        var groups = shards.GroupBy(s => s.Header.FileId).ToList();
        if (outputPath is not null && groups.Count > 1)
            throw new ShardDecodeException("The images belong to multiple different files; omit -o or decode them separately.");

        var restored = new List<RestoredFile>();
        foreach (var group in groups)
            restored.Add(Reassemble([.. group], outputPath, log));
        return restored;
    }

    private RestoredFile Reassemble(List<DecodedShard> shards, string? outputPath, Action<string> log)
    {
        var first = shards[0].Header;
        int count = first.Count;
        foreach (var s in shards)
            if (s.Header.Count != count)
                throw new ShardDecodeException($"Inconsistent shard set for '{first.FileName}': image counts differ.");

        // Both reassembly paths allocate the exact output size up front, so bound it first.
        if (first.TotalLength is < 0 or > ShardEncoder.MaxFileBytes || first.OriginalLength is < 0 or > ShardEncoder.MaxFileBytes)
            throw new ShardDecodeException($"'{first.FileName}': shard header declares an implausible file size.");

        byte[] data = first.StripeParity > 0
            ? parityReassembler.ReassembleWithParity(shards, first, log)
            : ReassembleContiguous(shards, first);

        if ((first.Flags & ShardHeader.FlagCompressed) != 0)
        {
            try
            {
                data = Inflate(data, (int)first.OriginalLength);
            }
            catch (InvalidDataException)
            {
                throw new ShardDecodeException($"'{first.FileName}': the reassembled stream failed to decompress. A shard is corrupt beyond recovery.");
            }
        }

        byte[] sha = SHA256.HashData(data);
        if (!sha.AsSpan().SequenceEqual(first.Sha256))
            throw new ShardDecodeException($"'{first.FileName}': SHA-256 of the reassembled file does not match the original. A shard was corrupted.");

        string outPath = outputPath ?? Path.Combine(Environment.CurrentDirectory, first.FileName);
        if (outputPath is null && File.Exists(outPath))
            outPath = Path.Combine(Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(first.FileName)}.restored{Path.GetExtension(first.FileName)}");
        File.WriteAllBytes(outPath, data);
        log($"  SHA-256 verified ✓  '{first.FileName}' → {outPath} ({data.Length:N0} bytes)");
        return new RestoredFile(first.FileName, outPath, data.LongLength);
    }

    /// <summary>Original path: no cross-shard parity — every data image must be present.</summary>
    private static byte[] ReassembleContiguous(List<DecodedShard> shards, ShardHeader first)
    {
        var byIndex = new DecodedShard?[first.Count];
        foreach (var s in shards)
            if (!s.Header.IsParity)
                byIndex[s.Header.Index] ??= s;

        var missing = Enumerable.Range(0, first.Count).Where(i => byIndex[i] is null).ToList();
        if (missing.Count > 0)
            throw new ShardDecodeException(
                $"'{first.FileName}': missing image(s) {string.Join(", ", missing.Select(i => i + 1))} of {first.Count}. " +
                "Capture them and decode again.");

        if (byIndex.Sum(s => (long)s!.Payload.Length) != first.TotalLength)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

        var data = new byte[first.TotalLength];
        int offset = 0;
        foreach (var s in byIndex)
        {
            s!.Payload.CopyTo(data.AsSpan(offset));
            offset += s.Payload.Length;
        }
        return data;
    }

    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        // The original length is known from the (CRC-validated) header, so decompress straight
        // into an exact-size buffer — no MemoryStream doubling, no final ToArray copy. Any
        // length lie is caught by the SHA-256 verification that follows.
        using var input = new MemoryStream(data);
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        var result = new byte[expectedLength];
        int offset = 0;
        while (offset < result.Length)
        {
            int n = ds.Read(result, offset, result.Length - offset);
            if (n == 0)
                break;
            offset += n;
        }
        return result;
    }
}
