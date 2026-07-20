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
    public PhotoFusion() : this(new Fec(), new Crc())
    {
    }

    public List<DecodedShard> Fuse(IReadOnlyList<FailedCapture> failures, Action<string> log)
    {
        var fused = new List<DecodedShard>();
        var groups = failures
            .Where(f => f.Layout.EccParity > 0)
            .GroupBy(f => (f.Layout.GridW, f.Layout.GridH, f.Layout.BitsPerCell, f.Layout.EccParity));

        foreach (var group in groups)
        {
            var captures = group.ToList();
            if (captures.Count < 2)
                continue;

            var layout = captures[0].Layout;
            var buffers = captures.Select(c => c.Cells).ToList();
            var stream = new byte[layout.CodewordCount * Fec.DataLength(layout.EccParity)];
            int correctedBytes = 0;

            // Per-codeword selection with a per-byte majority vote works from three captures up.
            // With exactly two, the interleaver has smeared any damage blob across every
            // codeword, so neither capture holds a clean copy of anything — instead cluster the
            // disagreement REGIONS spatially and hypothesis-test which capture is right per
            // cluster (glare sits in different places in different shots; the CRC gates truth).
            bool recovered =
                (captures.Count >= 3 && fec.TryRecoverFused(buffers, layout.EccParity, layout.CodewordCount, stream, out correctedBytes))
                || (captures.Count == 2 && TryClusterHypotheses(buffers[0], buffers[1], layout, stream, out correctedBytes));
            if (!recovered)
                continue;

            var header = ShardHeader.Deserialize(stream, out int headerLen);
            if (header is null || headerLen + header.PayloadLength > stream.Length)
                continue;
            byte[] payload = stream[headerLen..(headerLen + header.PayloadLength)];
            if (crc.Crc32(payload) != header.PayloadCrc32)
                continue;

            string sources = string.Join(" + ", captures.Select(c => Path.GetFileName(c.SourceFile)));
            log($"  fused   {captures.Count} failed capture(s) into a valid shard ({sources}, ECC corrected {correctedBytes} bytes)");
            fused.Add(new DecodedShard(header, payload, $"fusion of {captures.Count} captures", layout.EccParity, correctedBytes));
        }
        return fused;
    }

    private const int MaxClusters = 6; // 2^6 - 2 = 62 hypothesis attempts at most

    /// <summary>
    /// Two-capture fusion: cluster the bytes where the captures disagree into spatial regions
    /// (damage is contiguous — a glare blob, a cursor), then try every assignment of "capture A
    /// right here, capture B right there". ECC absorbs residual noise and the caller's CRC
    /// check guards against a wrong assignment ever escaping.
    /// </summary>
    private bool TryClusterHypotheses(byte[] a, byte[] b, Layout layout, byte[] dest, out int correctedBytes)
    {
        correctedBytes = 0;
        int protectedBytes = layout.CodewordCount * Fec.CodewordLength;
        int bits = layout.BitsPerCell;

        // Coarse spatial buckets (8x8 cells) containing at least one disagreeing byte.
        const int bucketCells = 8;
        int bw = (layout.GridW + bucketCells - 1) / bucketCells;
        int bh = (layout.GridH + bucketCells - 1) / bucketCells;
        var bucketOf = new Dictionary<int, List<int>>(); // bucket id -> disagreeing byte indices
        for (int i = 0; i < protectedBytes && i < a.Length && i < b.Length; i++)
        {
            if (a[i] == b[i])
                continue;
            long cell = (long)i * 8 / bits;
            int gx = (int)(cell % layout.GridW), gy = (int)(cell / layout.GridW);
            int bucket = gy / bucketCells * bw + gx / bucketCells;
            (bucketOf.TryGetValue(bucket, out var list) ? list : bucketOf[bucket] = []).Add(i);
        }
        if (bucketOf.Count == 0)
            return false; // identical captures — they failed for the same reason, nothing to fuse

        // Connected components over the occupied buckets (8-neighborhood).
        var clusterIds = new Dictionary<int, int>();
        int clusters = 0;
        foreach (int seed in bucketOf.Keys)
        {
            if (clusterIds.ContainsKey(seed))
                continue;
            if (clusters == MaxClusters)
                return false; // scattered disagreement (e.g. two different shards) — give up
            var frontier = new Stack<int>();
            frontier.Push(seed);
            clusterIds[seed] = clusters;
            while (frontier.Count > 0)
            {
                int cur = frontier.Pop();
                int cx = cur % bw, cy = cur / bw;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh)
                            continue;
                        int neighbor = ny * bw + nx;
                        if (bucketOf.ContainsKey(neighbor) && clusterIds.TryAdd(neighbor, clusterIds[cur]))
                            frontier.Push(neighbor);
                    }
                }
            }
            clusters++;
        }

        var clusterBytes = new List<int>[clusters];
        for (int c = 0; c < clusters; c++)
            clusterBytes[c] = [];
        foreach (var (bucket, indices) in bucketOf)
            clusterBytes[clusterIds[bucket]].AddRange(indices);

        // Hypotheses: per cluster, take B's bytes instead of A's. Masks 0 (= pure A) and full
        // (= pure B) are skipped — both already failed on their own.
        var candidate = new byte[a.Length];
        for (int mask = 1; mask < (1 << clusters) - 1; mask++)
        {
            a.CopyTo(candidate, 0);
            for (int c = 0; c < clusters; c++)
                if ((mask & (1 << c)) != 0)
                    foreach (int i in clusterBytes[c])
                        candidate[i] = b[i];

            if (fec.TryRecoverInto(candidate, layout.EccParity, layout.CodewordCount, dest, out correctedBytes))
                return true;
        }
        return false;
    }
}
