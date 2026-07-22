namespace QrShard;

/// <summary>
/// Cross-shard-parity reassembly: reconstructs missing data images from parity images stripe
/// by stripe, plus the completeness check that shares its stripe math.
/// </summary>
internal sealed class ParityReassembler(CrossShardFec crossShardFec, FountainFec fountainFec) : IParityReassembler
{
    public ParityReassembler() : this(new CrossShardFec(), new FountainFec())
    {
    }

    // Mirrors ShardHeader's ceiling on the parity ordinal space (stripes*StripeParity), so the
    // reassembler is total on directly-constructed shards too, not only on deserialized ones.
    private const long MaxParityOrdinals = 100_000_000;

    /// <summary>True when the stripe geometry can be reassembled without dividing by zero,
    /// overflowing int, or allocating an absurd ordinal array.</summary>
    private static bool StripeGeometryUsable(int count, int stripeData, int stripeParity)
    {
        if (count < 1 || stripeParity < 0)
            return false;
        if (stripeParity == 0)
            return true; // no cross-shard code — stripe width is unused
        if (stripeData < 1)
            return false;
        long stripes = ((long)count + stripeData - 1) / stripeData;
        return stripes * (long)stripeParity <= MaxParityOrdinals;
    }

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

            // Defense in depth: ShardHeader.Deserialize already bounds the geometry, but a
            // DecodedShard can be constructed directly (tests, future callers). These fields drive
            // divisor and array-size math below, so a malformed stripe set is simply not complete
            // — never a DivideByZero or OverflowException.
            if (!StripeGeometryUsable(count, s, p))
                return false;

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

            if ((first.Flags & ShardHeader.FlagFountain) != 0)
            {
                if (!IsFountainSetComplete(group, first, dataPresent))
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

    /// <summary>Fountain stripes solve when the available equations (identity rows for present
    /// data images + the coded frames' coefficient rows) reach full rank.</summary>
    private bool IsFountainSetComplete(IEnumerable<DecodedShard> group, ShardHeader first, bool[] dataPresent)
    {
        int count = first.Count, s = first.StripeData;
        int stripes = (count + s - 1) / s;

        var codedSeqs = new List<int>[stripes];
        for (int g = 0; g < stripes; g++)
            codedSeqs[g] = [];
        foreach (var x in group)
            if (x.Header.IsParity && x.Header.Index >= 0)
                codedSeqs[x.Header.Index % stripes].Add(x.Header.Index / stripes);

        for (int g = 0; g < stripes; g++)
        {
            int firstIndex = g * s;
            int stripeData = Math.Min(s, count - firstIndex);
            var rows = new List<byte[]>();
            for (int t = 0; t < stripeData; t++)
            {
                if (!dataPresent[firstIndex + t])
                    continue;
                var unit = new byte[stripeData];
                unit[t] = 1;
                rows.Add(unit);
            }
            foreach (int seq in codedSeqs[g])
                rows.Add(fountainFec.Coefficients(first.FileId, g, seq, stripeData));
            if (fountainFec.Rank(rows, stripeData) < stripeData)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Tolerates losing up to StripeParity images per stripe. Returns the per-image chunks
    /// (each <paramref name="chunkCapacity"/> bytes or the original payload) so the assembler
    /// can stream them out without materializing the whole file.
    /// </summary>
    public byte[][] ReassembleWithParity(List<DecodedShard> shards, ShardHeader first, Action<string> log,
        out int chunkCapacity)
    {
        if ((first.Flags & ShardHeader.FlagFountain) != 0)
            return ReassembleFountain(shards, first, log, out chunkCapacity);
        int count = first.Count, s = first.StripeData, p = first.StripeParity;
        if (!StripeGeometryUsable(count, s, p))
            throw new ShardDecodeException($"'{first.FileName}': shard header declares invalid stripe geometry.");
        int stripes = (count + s - 1) / s;
        int cap = shards.Max(x => x.Payload.Length); // full per-image capacity (parity images are always full)

        var dataByIndex = new DecodedShard?[count];
        var parityByOrdinal = new DecodedShard?[stripes * p];
        foreach (var x in shards)
        {
            if (x.Header.IsParity)
            {
                if ((uint)x.Header.Index < (uint)parityByOrdinal.Length)
                    parityByOrdinal[x.Header.Index] ??= x;
            }
            else if ((uint)x.Header.Index < (uint)count) // guard crafted out-of-range ordinals
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

            if (have < sData || !crossShardFec.TryReconstruct(present, sData, p, cap, out byte[][] recovered))
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

        chunkCapacity = cap;
        return chunks;
    }

    /// <summary>Fountain reassembly: solve each stripe from any full-rank frame subset.</summary>
    private byte[][] ReassembleFountain(List<DecodedShard> shards, ShardHeader first, Action<string> log,
        out int chunkCapacity)
    {
        int count = first.Count, s = first.StripeData;
        if (!StripeGeometryUsable(count, s, first.StripeParity))
            throw new ShardDecodeException($"'{first.FileName}': shard header declares invalid stripe geometry.");
        int stripes = (count + s - 1) / s;
        int cap = shards.Max(x => x.Payload.Length); // coded frames are always full capacity

        var dataByIndex = new DecodedShard?[count];
        var codedByStripe = new List<(int Seq, byte[] Payload)>[stripes];
        for (int g = 0; g < stripes; g++)
            codedByStripe[g] = [];
        foreach (var x in shards)
        {
            if (x.Header.IsParity)
            {
                if (x.Header.Index >= 0)
                    codedByStripe[x.Header.Index % stripes].Add((x.Header.Index / stripes, x.Payload));
            }
            else if ((uint)x.Header.Index < (uint)count) // guard crafted out-of-range ordinals
            {
                dataByIndex[x.Header.Index] ??= x;
            }
        }

        var chunks = new byte[count][];
        var unrecoverable = new List<int>();
        int reconstructed = 0;

        for (int g = 0; g < stripes; g++)
        {
            int firstIndex = g * s;
            int stripeData = Math.Min(s, count - firstIndex);

            bool allPresent = true;
            for (int t = 0; t < stripeData; t++)
                allPresent &= dataByIndex[firstIndex + t] is not null;
            if (allPresent)
            {
                for (int t = 0; t < stripeData; t++)
                    chunks[firstIndex + t] = Pad(dataByIndex[firstIndex + t]!.Payload, cap);
                continue;
            }

            // Systematic rows first, so present chunks pass through unchanged.
            var rows = new List<(byte[] Coef, byte[] Payload)>();
            for (int t = 0; t < stripeData; t++)
            {
                var shard = dataByIndex[firstIndex + t];
                if (shard is null)
                    continue;
                var unit = new byte[stripeData];
                unit[t] = 1;
                rows.Add((unit, Pad(shard.Payload, cap)));
            }
            foreach (var (seq, payload) in codedByStripe[g].OrderBy(c => c.Seq))
                rows.Add((fountainFec.Coefficients(first.FileId, g, seq, stripeData), payload));

            if (!fountainFec.TryReconstruct(rows, stripeData, cap, out byte[][] recovered))
            {
                for (int t = 0; t < stripeData; t++)
                    if (dataByIndex[firstIndex + t] is null)
                        unrecoverable.Add(firstIndex + t);
                continue;
            }

            for (int t = 0; t < stripeData; t++)
            {
                chunks[firstIndex + t] = recovered[t];
                if (dataByIndex[firstIndex + t] is null)
                    reconstructed++;
            }
        }

        if (unrecoverable.Count > 0)
            throw new ShardDecodeException(
                $"'{first.FileName}': {unrecoverable.Count} data image(s) are missing and the captured fountain frames " +
                $"do not span them (images {string.Join(", ", unrecoverable.Take(10).Select(i => i + 1))}{(unrecoverable.Count > 10 ? ", ..." : "")} of {count}). " +
                "Capture more frames and decode again.");

        if (reconstructed > 0)
            log($"  recovered {reconstructed} missing image(s) from fountain frames");

        long lastLen = first.TotalLength - (long)(count - 1) * cap;
        if (lastLen < 0 || lastLen > cap)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

        chunkCapacity = cap;
        return chunks;
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
