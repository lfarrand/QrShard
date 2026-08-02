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
    private static bool StripeGeometryUsable(int count, int stripeData, int stripeParity, bool fountain)
    {
        if (count < 1 || stripeParity < 0)
            return false;
        if (stripeParity == 0)
            return stripeData == 0; // no cross-shard code — both fields must be absent
        if (stripeData < 1)
            return false;
        if (fountain)
        {
            // SPEC section 8's reference profile caps fountain stripes at min(count, 64).
            // Bounding this here also keeps rank/inversion cubic work from a directly-constructed
            // (non-deserialized) shard set inside the reference solver's advertised limit.
            if (stripeData > Math.Min(count, FountainFec.MaxStripeData))
                return false;
        }
        else if ((long)stripeData + stripeParity > CrossShardFec.MaxShardsPerStripe)
        {
            // Cauchy x/y positions share one GF(2^8) domain. A larger sum wraps byte positions,
            // creates a zero denominator, and would make reconstruction throw.
            return false;
        }
        long stripes = ((long)count + stripeData - 1) / stripeData;
        return stripes * (long)stripeParity <= MaxParityOrdinals;
    }

    private static int StripeCount(int count, int stripeData) =>
        checked((int)(((long)count + stripeData - 1) / stripeData));

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
            List<DecodedShard> groupList = [.. group];
            var first = groupList[0].Header;
            if (groupList.Any(s => !first.HasSameFamilyAs(s.Header)))
                return false;
            int count = first.Count, s = first.StripeData, p = first.StripeParity;
            bool fountain = (first.Flags & ShardHeader.FlagFountain) != 0;

            // Defense in depth: ShardHeader.Deserialize already bounds the geometry, but a
            // DecodedShard can be constructed directly (tests, future callers). These fields drive
            // divisor and array-size math below, so a malformed stripe set is simply not complete
            // — never a DivideByZero or OverflowException.
            if (!StripeGeometryUsable(count, s, p, fountain))
                return false;

            int stripes = p == 0 ? 0 : StripeCount(count, s);
            int? parityOrdinalCount = fountain
                ? null
                : checked(stripes * p);
            CandidateMaps candidates = BuildCandidates(groupList, count, parityOrdinalCount);
            bool allDataPresent = candidates.UsableDataCount == count;

            // This is the exact fast path used by ShardAssembler: optional malformed parity does
            // not poison a complete data set, but the selected data chunks must concatenate to
            // the declared length before video/live capture is allowed to stop early.
            if (allDataPresent)
            {
                if (candidates.Data.Values.Sum(shard => (long)shard.Payload.Length) != first.TotalLength)
                    return false;
                continue;
            }
            if (p == 0)
                return false;

            // Every usable shard contributes at most one equation. Reject a clearly incomplete
            // set before any Count/stripe-sized structure or O(Count) walk; Count is untrusted and
            // video/live invokes this after each newly accepted frame.
            if ((long)candidates.UsableDataCount + candidates.UsableParityCount < count)
                return false;
            try
            {
                // Mirror recovery admission before the count/rank calculation. This performs no
                // Count x capacity allocation, but rejects poisoned ordinals and inconsistent
                // payload sizes that assembly would reject after an erroneous early stop.
                ValidateChunkCapacity(groupList, candidates, first,
                    parityOrdinalCount, out _);
            }
            catch (ShardDecodeException)
            {
                return false;
            }

            if (fountain)
            {
                if (!IsFountainSetComplete(candidates, first))
                    return false;
                continue;
            }

            if (!IsCauchyRecoverable(candidates, count, s, p, stripes))
                return false;
        }
        return true;
    }

    /// <summary>Fountain stripes solve when the available equations (identity rows for present
    /// data images + the coded frames' coefficient rows) reach full rank.</summary>
    private bool IsFountainSetComplete(CandidateMaps candidates, ShardHeader first)
    {
        int count = first.Count, s = first.StripeData;
        int stripes = StripeCount(count, s);

        // Sparse by stripe: a crafted Count of millions with one captured frame must not allocate
        // millions of empty List objects before the obvious incompleteness check can return.
        var codedSeqs = new Dictionary<int, List<int>>();
        foreach (int ordinal in candidates.Parity.Keys)
        {
            int stripe = ordinal % stripes;
            if (!codedSeqs.TryGetValue(stripe, out List<int>? seqs))
                codedSeqs.Add(stripe, seqs = []);
            seqs.Add(ordinal / stripes);
        }

        var touched = new HashSet<int>(codedSeqs.Keys);
        foreach (int index in candidates.Data.Keys)
            touched.Add(index / s);
        if (touched.Count < stripes)
            return false;

        foreach (int g in touched)
        {
            int firstIndex = g * s;
            int stripeData = Math.Min(s, count - firstIndex);

            IEnumerable<byte[]> Rows()
            {
                for (int t = 0; t < stripeData; t++)
                {
                    if (!candidates.Data.ContainsKey(firstIndex + t))
                        continue;
                    var unit = new byte[stripeData];
                    unit[t] = 1;
                    yield return unit;
                }
                if (codedSeqs.TryGetValue(g, out List<int>? sequences))
                    foreach (int seq in sequences)
                        yield return fountainFec.Coefficients(first.FileId, g, seq, stripeData);
            }

            if (fountainFec.Rank(Rows(), stripeData) < stripeData)
                return false;
        }
        return true;
    }

    private static bool IsCauchyRecoverable(CandidateMaps candidates, int count, int stripeData,
        int stripeParity, int stripes)
    {
        var availableByStripe = new Dictionary<int, int>();
        foreach (int index in candidates.Data.Keys)
        {
            int stripe = index / stripeData;
            availableByStripe[stripe] = availableByStripe.GetValueOrDefault(stripe) + 1;
        }
        foreach (int ordinal in candidates.Parity.Keys)
        {
            int stripe = ordinal / stripeParity;
            availableByStripe[stripe] = availableByStripe.GetValueOrDefault(stripe) + 1;
        }
        // An untouched stripe is unrecoverable. Check this before walking a Count-derived range.
        if (availableByStripe.Count < stripes)
            return false;
        foreach ((int stripe, int available) in availableByStripe)
        {
            int required = Math.Min(stripeData, count - stripe * stripeData);
            if (required < 1 || available < required)
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
        if (shards.Any(s => !first.HasSameFamilyAs(s.Header)))
            throw new ShardDecodeException(
                $"Inconsistent shard set for '{ShardHeader.Display(first.FileName)}': repeated file metadata differs.");
        if ((first.Flags & ShardHeader.FlagFountain) != 0)
            return ReassembleFountain(shards, first, log, out chunkCapacity);
        int count = first.Count, s = first.StripeData, p = first.StripeParity;
        if (!StripeGeometryUsable(count, s, p, fountain: false))
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares invalid stripe geometry.");
        int stripes = StripeCount(count, s);
        CandidateMaps candidates = BuildCandidates(shards, count, stripes * p);
        int cap = ValidateChunkCapacity(shards, candidates, first, stripes * p, out _);

        // Prove every stripe recoverable while representation is still sparse. This turns a
        // one-shard, Count=5,000,000 header into a small dictionary + typed error, rather than a
        // 5M data array plus a potentially 100M parity-ordinal array.
        if (!IsCauchyRecoverable(candidates, count, s, p, stripes))
            throw IncompleteSet(first, candidates,
                "are missing and beyond parity recovery", "Capture more of the missing images and decode again.");

        // Only a set proven recoverable earns a Count-sized result array.
        var chunks = new byte[count][];
        int reconstructed = count - candidates.UsableDataCount;

        for (int g = 0; g < stripes; g++)
        {
            int first0 = g * s;
            int sData = Math.Min(s, count - first0);
            var present = new byte[]?[sData + p];
            int have = 0;

            for (int t = 0; t < sData; t++)
            {
                if (candidates.Data.TryGetValue(first0 + t, out DecodedShard? shard))
                {
                    present[t] = Pad(shard.Payload, cap);
                    have++;
                }
            }
            for (int pi = 0; pi < p; pi++)
            {
                if (candidates.Parity.TryGetValue(g * p + pi, out DecodedShard? shard))
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
                throw new ShardDecodeException(
                    $"'{ShardHeader.Display(first.FileName)}': parity equations could not reconstruct a stripe that passed admission.");

            for (int t = 0; t < sData; t++)
            {
                chunks[first0 + t] = recovered[t];
            }
        }

        if (reconstructed > 0)
            log($"  recovered {reconstructed} missing image(s) from parity");

        long lastLen = first.TotalLength - (long)(count - 1) * cap;
        if (lastLen < 0 || lastLen > cap)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': reassembled length does not match expected {first.TotalLength:N0}.");

        chunkCapacity = cap;
        return chunks;
    }

    /// <summary>Fountain reassembly: solve each stripe from any full-rank frame subset.</summary>
    private byte[][] ReassembleFountain(List<DecodedShard> shards, ShardHeader first, Action<string> log,
        out int chunkCapacity)
    {
        int count = first.Count, s = first.StripeData;
        if (!StripeGeometryUsable(count, s, first.StripeParity, fountain: true))
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': shard header declares invalid stripe geometry.");
        int stripes = StripeCount(count, s);
        // Fountain sequence ordinals are intentionally unbounded by the originally emitted frame
        // count: a sender may mint additional equations later. They still must be non-negative
        // and use the same full payload capacity.
        CandidateMaps candidates = BuildCandidates(shards, count, parityOrdinalCount: null);
        int cap = ValidateChunkCapacity(shards, candidates, first, parityOrdinalCount: null, out _);
        if (!IsFountainSetComplete(candidates, first))
            throw IncompleteSet(first, candidates,
                "are missing and the captured fountain frames do not span them",
                "Capture more frames and decode again.");

        var codedByStripe = new Dictionary<int, List<(int Seq, byte[] Payload)>>();
        foreach ((int ordinal, DecodedShard shard) in candidates.Parity)
        {
            int stripe = ordinal % stripes;
            if (!codedByStripe.TryGetValue(stripe, out List<(int Seq, byte[] Payload)>? rows))
                codedByStripe.Add(stripe, rows = []);
            rows.Add((ordinal / stripes, shard.Payload));
        }

        var chunks = new byte[count][];
        int reconstructed = count - candidates.UsableDataCount;

        for (int g = 0; g < stripes; g++)
        {
            int firstIndex = g * s;
            int stripeData = Math.Min(s, count - firstIndex);

            bool allPresent = true;
            for (int t = 0; t < stripeData; t++)
                allPresent &= candidates.Data.ContainsKey(firstIndex + t);
            if (allPresent)
            {
                for (int t = 0; t < stripeData; t++)
                    chunks[firstIndex + t] = Pad(candidates.Data[firstIndex + t].Payload, cap);
                continue;
            }

            // Systematic rows first, so present chunks pass through unchanged. Yield coefficients
            // lazily: the solver admits at most stripeData independent rows and then stops, so a
            // capture containing surplus unique fountain frames cannot allocate one coefficient
            // array per frame before rank admission begins.
            IEnumerable<(byte[] Coef, byte[] Payload)> Rows()
            {
                for (int t = 0; t < stripeData; t++)
                {
                    if (!candidates.Data.TryGetValue(firstIndex + t, out DecodedShard? shard))
                        continue;
                    var unit = new byte[stripeData];
                    unit[t] = 1;
                    yield return (unit, Pad(shard.Payload, cap));
                }
                if (codedByStripe.TryGetValue(g, out List<(int Seq, byte[] Payload)>? coded))
                    foreach (var (seq, payload) in coded)
                        yield return (fountainFec.Coefficients(first.FileId, g, seq, stripeData), payload);
            }

            if (!fountainFec.TryReconstruct(Rows(), stripeData, cap, out byte[][] recovered))
                throw new ShardDecodeException(
                    $"'{ShardHeader.Display(first.FileName)}': fountain equations could not reconstruct a stripe that passed admission.");

            for (int t = 0; t < stripeData; t++)
            {
                chunks[firstIndex + t] = recovered[t];
            }
        }

        if (reconstructed > 0)
            log($"  recovered {reconstructed} missing image(s) from fountain frames");

        long lastLen = first.TotalLength - (long)(count - 1) * cap;
        if (lastLen < 0 || lastLen > cap)
            throw new ShardDecodeException($"'{ShardHeader.Display(first.FileName)}': reassembled length does not match expected {first.TotalLength:N0}.");

        chunkCapacity = cap;
        return chunks;
    }

    /// <summary>
    /// Sparse, conflict-aware view of a shard family. Exact duplicates collapse to one candidate;
    /// two different CRC-valid payloads for the same ordinal make that ordinal an erasure. Once an
    /// ordinal conflicts no number of later copies can make one untrusted alternative win.
    /// </summary>
    private sealed record CandidateMaps(
        Dictionary<int, DecodedShard> Data,
        Dictionary<int, DecodedShard> Parity,
        HashSet<int> DataConflicts,
        HashSet<int> ParityConflicts)
    {
        internal int UsableDataCount => Data.Count;
        internal int UsableParityCount => Parity.Count;
    }

    private static CandidateMaps BuildCandidates(IEnumerable<DecodedShard> shards, int dataCount,
        int? parityOrdinalCount)
    {
        var data = new Dictionary<int, DecodedShard>();
        var parity = new Dictionary<int, DecodedShard>();
        var dataConflicts = new HashSet<int>();
        var parityConflicts = new HashSet<int>();
        foreach (DecodedShard shard in shards)
        {
            if (shard.Header.IsParity)
            {
                int ordinal = shard.Header.Index;
                if (ordinal < 0 || (parityOrdinalCount is not null && ordinal >= parityOrdinalCount.Value))
                    continue;
                AddCandidate(parity, parityConflicts, ordinal, shard);
            }
            else
            {
                int ordinal = shard.Header.Index;
                if ((uint)ordinal >= (uint)dataCount)
                    continue;
                AddCandidate(data, dataConflicts, ordinal, shard);
            }
        }
        return new CandidateMaps(data, parity, dataConflicts, parityConflicts);
    }

    private static void AddCandidate(Dictionary<int, DecodedShard> candidates, HashSet<int> conflicts,
        int ordinal, DecodedShard shard)
    {
        if (conflicts.Contains(ordinal))
            return; // conflict is terminal; do not retain or compare unlimited alternatives
        if (!candidates.TryGetValue(ordinal, out DecodedShard? existing))
        {
            candidates.Add(ordinal, shard);
            return;
        }
        bool identical = existing.Header.PayloadLength == shard.Header.PayloadLength &&
            existing.Header.PayloadCrc32 == shard.Header.PayloadCrc32 &&
            existing.Payload.AsSpan().SequenceEqual(shard.Payload);
        if (identical)
            return;
        candidates.Remove(ordinal);
        conflicts.Add(ordinal);
    }

    private static ShardDecodeException IncompleteSet(ShardHeader first, CandidateMaps candidates,
        string reason, string action)
    {
        int missing = first.Count - candidates.UsableDataCount;
        string preview = MissingPreview(candidates.Data, first.Count);
        string conflict = candidates.DataConflicts.Count == 0 && candidates.ParityConflicts.Count == 0
            ? ""
            : $" Conflicting copies were treated as erasures ({candidates.DataConflicts.Count:N0} data, " +
              $"{candidates.ParityConflicts.Count:N0} parity ordinal(s)).";
        return new ShardDecodeException(
            $"'{ShardHeader.Display(first.FileName)}': {missing:N0} data image(s) {reason} " +
            $"(images {preview} of {first.Count:N0}).{conflict} {action}");
    }

    internal static string MissingPreview(IReadOnlyDictionary<int, DecodedShard> present, int count,
        int maximum = 10)
    {
        var preview = new List<int>(Math.Min(maximum, count));
        for (int i = 0; i < count && preview.Count < maximum; i++)
            if (!present.ContainsKey(i))
                preview.Add(i + 1);
        string text = string.Join(", ", preview);
        return count - present.Count > preview.Count ? text + ", ..." : text;
    }

    /// <summary>
    /// Derives the full chunk capacity only from shards that must carry a full chunk, then
    /// validates every present length and the implied final length before Pad/recovery arrays are
    /// allocated. Taking Max(payload.Length) let one oversized crafted parity shard amplify
    /// Count x cap allocations before the old late length check.
    /// </summary>
    private static int ValidateChunkCapacity(List<DecodedShard> shards, CandidateMaps candidates,
        ShardHeader first, int? parityOrdinalCount, out int lastLength)
    {
        int? capacity = null;
        foreach (DecodedShard shard in shards)
        {
            // A conflicting ordinal is an erasure, including when its alternatives disagree in
            // length. Neither untrusted alternative may establish or invalidate global capacity;
            // usable siblings/parity establish it and reconstruction supplies the erased chunk.
            if (shard.Header.IsParity
                    ? candidates.ParityConflicts.Contains(shard.Header.Index)
                    : candidates.DataConflicts.Contains(shard.Header.Index))
                continue;
            bool fullChunk = shard.Header.IsParity
                ? shard.Header.Index >= 0 &&
                  (parityOrdinalCount is null || shard.Header.Index < parityOrdinalCount.Value)
                : (uint)shard.Header.Index < (uint)Math.Max(0, first.Count - 1);
            if (!fullChunk)
                continue;
            if (capacity is null)
                capacity = shard.Payload.Length;
            else if (capacity.Value != shard.Payload.Length)
                throw new ShardDecodeException(
                    $"'{ShardHeader.Display(first.FileName)}': shard payload capacities are inconsistent.");
        }

        if (capacity is null || capacity < 1)
            throw new ShardDecodeException(
                $"'{ShardHeader.Display(first.FileName)}': no valid full-size shard is available to establish recovery geometry.");
        int cap = capacity.Value;
        long impliedLast = first.TotalLength - (long)(first.Count - 1) * cap;
        if (impliedLast < (first.Count == 1 ? 0 : 1) || impliedLast > cap)
            throw new ShardDecodeException(
                $"'{ShardHeader.Display(first.FileName)}': shard capacity is inconsistent with the declared total length.");
        lastLength = (int)impliedLast;

        foreach (DecodedShard shard in shards)
        {
            if (shard.Header.IsParity
                    ? candidates.ParityConflicts.Contains(shard.Header.Index)
                    : candidates.DataConflicts.Contains(shard.Header.Index))
                continue;
            if (shard.Header.IsParity)
            {
                bool validOrdinal = shard.Header.Index >= 0 &&
                    (parityOrdinalCount is null || shard.Header.Index < parityOrdinalCount.Value);
                if (!validOrdinal || shard.Payload.Length != cap)
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}': parity shard ordinal or payload length is invalid.");
            }
            else
            {
                if ((uint)shard.Header.Index >= (uint)first.Count)
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}': data shard ordinal is invalid.");
                int expected = shard.Header.Index == first.Count - 1 ? lastLength : cap;
                if (shard.Payload.Length != expected)
                    throw new ShardDecodeException(
                        $"'{ShardHeader.Display(first.FileName)}': data shard payload length is inconsistent with its ordinal.");
            }
        }
        return cap;
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
