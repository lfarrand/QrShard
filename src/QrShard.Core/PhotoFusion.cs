namespace QrShard;

/// <summary>Reconstructs shards from multiple individually-failed captures of the same image.</summary>
internal interface IPhotoFusion
{
    List<DecodedShard> Fuse(IReadOnlyList<FailedCapture> failures, Action<string> log);
}

/// <summary>
/// Multi-capture fusion: when several photos of the same shard each fail ECC on their own
/// (glare, reflections — damage that moves between shots), their sampled cell streams are
/// combined codeword by codeword. A codeword takes the first capture whose copy corrects, with
/// a per-byte majority vote as the fallback for three or more captures. Captures are grouped by
/// layout signature; a group only fuses into a valid shard when its members really show the
/// same image, because the payload CRC still gates the result.
/// </summary>
internal sealed class PhotoFusion(Fec fec, Crc crc) : IPhotoFusion
{
    // More captures cease to add practical value and made the per-byte majority fallback's work
    // and retained memory attacker-controlled. Eight leaves ample independent glare positions.
    internal const int MaxCapturesPerGroup = 8;
    internal const int MaxFusionGroups = 1_024;

    public PhotoFusion() : this(new Fec(), new Crc())
    {
    }

    public List<DecodedShard> Fuse(IReadOnlyList<FailedCapture> failures, Action<string> log)
    {
        var fused = new List<DecodedShard>();
        var groups = new Dictionary<(int GridW, int GridH, int Bits, int Ecc, bool Interleave2), List<FailedCapture>>();
        int skipped = 0;
        long remainingClusterWork = MaxClusterHypothesisWork;
        foreach (FailedCapture failure in failures)
        {
            if (failure.Layout.EccParity <= 0)
                continue;
            long required = (long)failure.Layout.CodewordCount * Fec.CodewordLength;
            if (required <= 0 || required > failure.Cells.Length)
            {
                skipped++;
                continue;
            }
            var key = (failure.Layout.GridW, failure.Layout.GridH, failure.Layout.BitsPerCell,
                failure.Layout.EccParity, failure.Layout.Interleave2);
            if (!groups.TryGetValue(key, out List<FailedCapture>? captures))
            {
                if (groups.Count >= MaxFusionGroups)
                {
                    skipped++;
                    continue;
                }
                captures = [];
                groups.Add(key, captures);
            }
            if (captures.Count < MaxCapturesPerGroup)
                captures.Add(failure);
            else
                skipped++;
        }

        foreach (List<FailedCapture> captures in groups.Values)
        {
            if (captures.Count < 2)
                continue;

            var layout = captures[0].Layout;
            var buffers = captures.Select(c => c.Cells).ToList();
            byte[] stream;
            int correctedBytes = 0;

            // Per-codeword selection with a per-byte majority vote works from three captures up.
            // With exactly two, the interleaver has smeared any damage blob across every
            // codeword, so neither capture holds a clean copy of anything — instead cluster the
            // disagreement REGIONS spatially and hypothesis-test which capture is right per
            // cluster (glare sits in different places in different shots; the CRC gates truth).
            bool workRefused = false;
            bool recovered;
            if (captures.Count >= 3)
            {
                stream = new byte[layout.CodewordCount * Fec.DataLength(layout.EccParity)];
                recovered = fec.TryRecoverFused(buffers, layout.EccParity, layout.CodewordCount,
                    stream, out correctedBytes);
            }
            else
            {
                recovered = TryClusterHypotheses(buffers[0], buffers[1], layout,
                    ref remainingClusterWork, out workRefused, out stream, out correctedBytes);
            }
            if (workRefused)
                skipped += captures.Count;
            if (!recovered)
                continue;

            var header = ShardHeader.Deserialize(stream, out int headerLen);
            if (header is null || (long)headerLen + header.PayloadLength > stream.Length) // long: crafted-length safe
                continue;
            byte[] payload = stream[headerLen..(headerLen + header.PayloadLength)];
            if (crc.Crc32(payload) != header.PayloadCrc32)
                continue;

            string sources = string.Join(" + ", captures.Select(c =>
                ShardHeader.Display(Path.GetFileName(c.SourceFile))));
            log($"  fused   {captures.Count} failed capture(s) into a valid shard ({sources}, ECC corrected {correctedBytes} bytes)");
            fused.Add(new DecodedShard(header, payload, $"fusion of {captures.Count} captures", layout.EccParity, correctedBytes));
        }
        if (skipped > 0)
            log($"  skipped {skipped:N0} failed capture(s) outside bounded fusion input/work limits");
        return fused;
    }

    private const int MaxClusters = 6; // 2^6 - 2 = 62 hypothesis attempts at most

    // Candidate synthesis and CRC validation are full protected-byte passes and RS syndrome
    // evaluation costs approximately one such pass per parity symbol. Without a work bound, a valid 16K metadata
    // layout with six disconnected disagreement regions could request 62 passes over an ~100 MB
    // stream (plus RS), pinning a decoder for minutes while remaining inside the memory budget.
    // 512 Mi work units admits the normal 4K/cell-3/parity-16 case even at six clusters, while
    // asking unusually dense captures to provide a third photo and use the linear majority path.
    internal const long MaxClusterHypothesisWork = 512L * 1024 * 1024;

    internal static bool IsClusterHypothesisWorkAllowed(int protectedBytes, int clusters, int parity)
    {
        if (protectedBytes <= 0 || clusters < 2 || clusters > MaxClusters ||
            parity <= 0 || parity > Fec.MaxParity)
            return false;
        return ClusterHypothesisWork(protectedBytes, clusters, parity) <= MaxClusterHypothesisWork;
    }

    private static long ClusterHypothesisWork(int protectedBytes, int clusters, int parity) =>
        checked((long)protectedBytes * ((1L << clusters) - 2) * (parity + 2L));

    internal static bool TryReserveClusterHypothesisWork(int protectedBytes, int clusters, int parity,
        ref long remainingWork)
    {
        if (!IsClusterHypothesisWorkAllowed(protectedBytes, clusters, parity))
            return false;
        long required = ClusterHypothesisWork(protectedBytes, clusters, parity);
        if (required > remainingWork)
            return false;
        remainingWork -= required;
        return true;
    }

    /// <summary>
    /// Two-capture fusion: cluster the bytes where the captures disagree into spatial regions
    /// (damage is contiguous — a glare blob, a cursor), then try every assignment of "capture A
    /// right here, capture B right there". ECC absorbs residual noise and the caller's CRC
    /// check guards against a wrong assignment ever escaping.
    /// </summary>
    private bool TryClusterHypotheses(byte[] a, byte[] b, Layout layout,
        ref long remainingWork, out bool workRefused, out byte[] recoveredStream,
        out int correctedBytes)
    {
        workRefused = false;
        recoveredStream = [];
        correctedBytes = 0;
        int protectedBytes = layout.CodewordCount * Fec.CodewordLength;
        int bits = layout.BitsPerCell;

        // Coarse spatial buckets (8x8 cells) containing at least one disagreeing byte. A dense
        // byte tag per bucket is at most protectedBytes/(8*bits); unlike Dictionary<int,List<int>>
        // it never retains one 4-byte boxed/list index for every disagreeing payload byte.
        const int bucketCells = 8;
        int bw = (layout.GridW + bucketCells - 1) / bucketCells;
        int bh = (layout.GridH + bucketCells - 1) / bucketCells;
        var bucketTags = new byte[checked(bw * bh)]; // 0 empty, 1 occupied, 2.. cluster+2
        bool anyDifference = false;
        for (int i = 0; i < protectedBytes; i++)
        {
            if (a[i] == b[i])
                continue;
            anyDifference = true;
            long cell = (long)i * 8 / bits;
            int gx = (int)(cell % layout.GridW), gy = (int)(cell / layout.GridW);
            int bucket = gy / bucketCells * bw + gx / bucketCells;
            bucketTags[bucket] = 1;
        }
        if (!anyDifference)
            return false; // identical captures — they failed for the same reason, nothing to fuse

        // Connected components over occupied buckets (8-neighborhood). One fixed frontier is
        // bounded by the bucket grid; it replaces an unbounded Stack plus per-bucket dictionaries.
        var frontier = new int[bucketTags.Length];
        int clusters = 0;
        for (int seed = 0; seed < bucketTags.Length; seed++)
        {
            if (bucketTags[seed] != 1)
                continue;
            if (clusters == MaxClusters)
                return false; // scattered disagreement (e.g. two different shards) — give up
            byte clusterTag = checked((byte)(clusters + 2));
            int head = 0, tail = 0;
            frontier[tail++] = seed;
            bucketTags[seed] = clusterTag;
            while (head < tail)
            {
                int cur = frontier[head++];
                int cx = cur % bw, cy = cur / bw;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh)
                            continue;
                        int neighbor = ny * bw + nx;
                        if (bucketTags[neighbor] == 1)
                        {
                            bucketTags[neighbor] = clusterTag;
                            frontier[tail++] = neighbor;
                        }
                    }
                }
            }
            clusters++;
        }

        if (clusters < 2)
            return false; // only pure A/B exist, and both already failed individually

        // Refuse the exponential two-shot fallback before allocating its byte-level tags or
        // candidate buffer. The caller can still recover through the bounded linear path after a
        // third capture; silently trying only an arbitrary prefix of masks would bias correctness.
        // Reserve the group's exact exhaustive mask count before the first attempt. Charging the
        // worst case, rather than refunding after an early RS-valid recovery, ensures all groups
        // in this Fuse invocation collectively remain below the same deterministic ceiling.
        if (!TryReserveClusterHypothesisWork(protectedBytes, clusters, layout.EccParity,
                ref remainingWork))
        {
            workRefused = true;
            return false;
        }

        // Do not allocate the potentially very large recovered stream for groups refused by the
        // aggregate work gate. It is part of the admitted fusion working set, not admission work.
        recoveredStream = new byte[layout.CodewordCount * Fec.DataLength(layout.EccParity)];
        const byte Same = byte.MaxValue;
        var byteCluster = new byte[protectedBytes];
        Array.Fill(byteCluster, Same);
        for (int i = 0; i < protectedBytes; i++)
        {
            if (a[i] == b[i])
                continue;
            long cell = (long)i * 8 / bits;
            int gx = (int)(cell % layout.GridW), gy = (int)(cell / layout.GridW);
            int bucket = gy / bucketCells * bw + gx / bucketCells;
            byteCluster[i] = checked((byte)(bucketTags[bucket] - 2));
        }

        // Hypotheses: per cluster, take B's bytes instead of A's. Masks 0 (= pure A) and full
        // (= pure B) are skipped — both already failed on their own.
        var candidate = new byte[protectedBytes];
        for (int mask = 1; mask < (1 << clusters) - 1; mask++)
        {
            for (int i = 0; i < protectedBytes; i++)
            {
                byte cluster = byteCluster[i];
                candidate[i] = cluster != Same && (mask & (1 << cluster)) != 0 ? b[i] : a[i];
            }

            // RS validity is necessary but not sufficient: multiple spatial assignments can be
            // valid codewords. Keep searching until the embedded header and payload CRC also
            // agree, otherwise an earlier wrong-yet-RS-valid mask suppresses the true later mask.
            if (fec.TryRecoverInto(candidate, layout.EccParity, layout.CodewordCount, recoveredStream,
                    out correctedBytes, stopAfterFirstFailure: true) && HasValidPayloadCrc(recoveredStream))
                return true;
        }
        return false;
    }

    private bool HasValidPayloadCrc(byte[] stream)
    {
        ShardHeader? header = ShardHeader.Deserialize(stream, out int headerLen);
        return header is not null && (long)headerLen + header.PayloadLength <= stream.Length &&
            crc.Crc32(stream.AsSpan(headerLen, header.PayloadLength)) == header.PayloadCrc32;
    }
}
