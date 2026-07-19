namespace QrShard;

/// <summary>
/// Cross-shard-parity reassembly: reconstructs missing data images from parity images stripe
/// by stripe, plus the completeness check that shares its stripe math.
/// </summary>
internal sealed class ParityReassembler : IParityReassembler
{
    /// <summary>
    /// True when every file in the shard set can be fully reassembled — all data images
    /// present, or (with cross-shard parity) every stripe holds at least StripeData of its
    /// StripeData+StripeParity images. Used by video decoding to stop consuming frames early.
    /// </summary>
    public bool IsSetComplete(IReadOnlyCollection<DecodedShard> shards)
    {
        if (shards.Count == 0)
            return false;

        foreach (var group in shards.GroupBy(s => s.Header.FileId))
        {
            var first = group.First().Header;
            int count = first.Count, s = first.StripeData, p = first.StripeParity;

            var dataPresent = new bool[count];
            foreach (var x in group)
                if (!x.Header.IsParity && x.Header.Index < count)
                    dataPresent[x.Header.Index] = true;

            if (p == 0)
            {
                if (Array.IndexOf(dataPresent, false) >= 0)
                    return false;
                continue;
            }

            int stripes = (count + s - 1) / s;
            var parityPresent = new bool[stripes * p]; // by ordinal, so duplicates don't double-count
            foreach (var x in group)
                if (x.Header.IsParity && x.Header.Index < parityPresent.Length)
                    parityPresent[x.Header.Index] = true;

            for (int g = 0; g < stripes; g++)
            {
                int firstIndex = g * s;
                int stripeData = Math.Min(s, count - firstIndex);
                int have = 0;
                for (int pi = 0; pi < p; pi++)
                    if (parityPresent[g * p + pi])
                        have++;
                for (int t = 0; t < stripeData; t++)
                    if (dataPresent[firstIndex + t])
                        have++;
                if (have < stripeData)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Tolerates losing up to StripeParity images per stripe.</summary>
    public byte[] ReassembleWithParity(List<DecodedShard> shards, ShardHeader first, Action<string> log)
    {
        int count = first.Count, s = first.StripeData, p = first.StripeParity;
        int stripes = (count + s - 1) / s;
        int cap = shards.Max(x => x.Payload.Length); // full per-image capacity (parity images are always full)

        var dataByIndex = new DecodedShard?[count];
        var parityByOrdinal = new DecodedShard?[stripes * p];
        foreach (var x in shards)
        {
            if (x.Header.IsParity)
            {
                if (x.Header.Index < parityByOrdinal.Length)
                    parityByOrdinal[x.Header.Index] ??= x;
            }
            else
            {
                dataByIndex[x.Header.Index] ??= x;
            }
        }

        var chunks = new byte[count][];
        var unrecoverable = new List<int>();
        int reconstructed = 0;

        for (int g = 0; g < stripes; g++)
        {
            int first0 = g * s;
            int sData = Math.Min(s, count - first0);
            var present = new byte[]?[sData + p];
            int have = 0;

            for (int t = 0; t < sData; t++)
            {
                var shard = dataByIndex[first0 + t];
                if (shard is not null)
                {
                    present[t] = Pad(shard.Payload, cap);
                    have++;
                }
            }
            for (int pi = 0; pi < p; pi++)
            {
                var shard = parityByOrdinal[g * p + pi];
                if (shard is not null)
                {
                    present[sData + pi] = shard.Payload; // already full length
                    have++;
                }
            }

            bool allDataPresent = Enumerable.Range(0, sData).All(t => present[t] is not null);
            if (allDataPresent)
            {
                for (int t = 0; t < sData; t++)
                    chunks[first0 + t] = present[t]!;
                continue;
            }

            if (have < sData || !CrossShardFec.TryReconstruct(present, sData, p, cap, out byte[][] recovered))
            {
                for (int t = 0; t < sData; t++)
                    if (dataByIndex[first0 + t] is null)
                        unrecoverable.Add(first0 + t);
                continue;
            }

            for (int t = 0; t < sData; t++)
            {
                chunks[first0 + t] = recovered[t];
                if (dataByIndex[first0 + t] is null)
                    reconstructed++;
            }
        }

        if (unrecoverable.Count > 0)
            throw new ShardDecodeException(
                $"'{first.FileName}': {unrecoverable.Count} data image(s) are missing and beyond parity recovery " +
                $"(images {string.Join(", ", unrecoverable.Take(10).Select(i => i + 1))}{(unrecoverable.Count > 10 ? ", ..." : "")} of {count}). " +
                "Capture more of the missing images and decode again.");

        if (reconstructed > 0)
            log($"  recovered {reconstructed} missing image(s) from parity");

        long lastLen = first.TotalLength - (long)(count - 1) * cap;
        if (lastLen < 0 || lastLen > cap)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

        var data = new byte[first.TotalLength]; // offsets fit int: TotalLength <= ShardEncoder.MaxFileBytes
        for (int i = 0; i < count; i++)
        {
            int len = i < count - 1 ? cap : (int)lastLen;
            chunks[i].AsSpan(0, Math.Min(len, chunks[i].Length)).CopyTo(data.AsSpan(i * cap));
        }
        return data;
    }

    private static byte[] Pad(byte[] src, int length)
    {
        if (src.Length == length)
            return src;
        var padded = new byte[length];
        Array.Copy(src, padded, Math.Min(src.Length, length));
        return padded;
    }
}
