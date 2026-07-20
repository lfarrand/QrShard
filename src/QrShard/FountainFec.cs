namespace QrShard;

/// <summary>
/// Fountain-style erasure coding for video mode: random-linear network coding over GF(2^8).
/// Every coded frame is a random linear combination of a stripe's data chunks, with
/// coefficients derived deterministically from (fileId, stripe, sequence) — so the frame's
/// header alone identifies its equation, and the sender can mint as MANY distinct coded frames
/// as it likes (unlike the Cauchy layer's 255-per-stripe ceiling). The receiver reconstructs a
/// stripe from ANY set of frames whose equations reach full rank — with random coefficients,
/// barely more than stripeData frames, whichever ones happened to survive capture.
/// </summary>
internal sealed class FountainFec(Gf256 gf)
{
    /// <summary>Stripe-size cap: keeps the receiver's k x k solve and rank checks cheap.</summary>
    public const int MaxStripeData = 64;

    public FountainFec() : this(new Gf256())
    {
    }

    /// <summary>Deterministic coefficient row for coded frame (stripe, seq) — SplitMix64 bytes.</summary>
    public byte[] Coefficients(ulong fileId, int stripe, int seq, int count)
    {
        var coef = new byte[count];
        ulong state = fileId ^ (0x9E3779B97F4A7C15UL * ((ulong)(uint)stripe * 1_000_003UL + (ulong)(uint)seq + 1));
        int filled = 0;
        while (filled < count)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            for (int b = 0; b < 8 && filled < count; b++, z >>= 8)
                coef[filled++] = (byte)z;
        }
        return coef;
    }

    /// <summary>Payload of coded frame (stripe, seq): the coefficient-weighted sum of the stripe's chunks.</summary>
    public byte[] EncodeFrame(ArraySegment<byte[]> chunks, ulong fileId, int stripe, int seq, int shardLen)
    {
        byte[] coef = Coefficients(fileId, stripe, seq, chunks.Count);
        var payload = new byte[shardLen];
        for (int t = 0; t < chunks.Count; t++)
            gf.MulAdd(coef[t], chunks[t], payload);
        return payload;
    }

    /// <summary>Rank of the coefficient rows over GF(2^8) — the stripe solves iff rank == dataCount.</summary>
    public int Rank(IReadOnlyList<byte[]> coefRows, int dataCount)
    {
        var reduced = new List<byte[]>();
        var pivots = new List<int>();
        foreach (byte[] row in coefRows)
        {
            if (reduced.Count == dataCount)
                break;
            byte[]? r = Reduce(row, reduced, pivots, dataCount);
            if (r is null)
                continue;
            reduced.Add(r);
            pivots.Add(Array.FindIndex(r, v => v != 0));
        }
        return reduced.Count;
    }

    /// <summary>
    /// Reconstructs a stripe's data chunks from any full-rank subset of (coefficients, payload)
    /// rows. Rows are considered in order, so callers list systematic (identity) rows first and
    /// the solver prefers original chunks over coded ones.
    /// </summary>
    public bool TryReconstruct(IReadOnlyList<(byte[] Coef, byte[] Payload)> rows, int dataCount, int shardLen,
        out byte[][] data)
    {
        data = [];
        var selected = new List<(byte[] Coef, byte[] Payload)>();
        var reduced = new List<byte[]>();
        var pivots = new List<int>();
        foreach (var row in rows)
        {
            if (selected.Count == dataCount)
                break;
            byte[]? r = Reduce(row.Coef, reduced, pivots, dataCount);
            if (r is null)
                continue; // linearly dependent on rows already selected
            reduced.Add(r);
            pivots.Add(Array.FindIndex(r, v => v != 0));
            selected.Add(row);
        }
        if (selected.Count < dataCount)
            return false;

        var matrix = new byte[dataCount][];
        for (int i = 0; i < dataCount; i++)
            matrix[i] = (byte[])selected[i].Coef.Clone();
        if (!gf.Invert(matrix, dataCount))
            return false; // cannot happen for a rank-selected set; guard anyway

        var result = new byte[dataCount][];
        System.Threading.Tasks.Parallel.For(0, dataCount, i =>
        {
            var outShard = new byte[shardLen];
            for (int j = 0; j < dataCount; j++)
                gf.MulAdd(matrix[i][j], selected[j].Payload, outShard);
            result[i] = outShard;
        });
        data = result;
        return true;
    }

    /// <summary>Row-reduces a copy of <paramref name="row"/> against the pivots; null if it vanishes.</summary>
    private byte[]? Reduce(byte[] row, List<byte[]> reduced, List<int> pivots, int dataCount)
    {
        var r = new byte[dataCount];
        row.AsSpan(0, dataCount).CopyTo(r);
        for (int k = 0; k < reduced.Count; k++)
        {
            byte factor = r[pivots[k]];
            if (factor == 0)
                continue;
            byte[] pivotRow = reduced[k];
            for (int j = 0; j < dataCount; j++)
                r[j] ^= gf.Mul(factor, pivotRow[j]);
        }
        int p = Array.FindIndex(r, v => v != 0);
        if (p < 0)
            return null;
        byte inv = gf.Inv(r[p]);
        for (int j = 0; j < dataCount; j++)
            r[j] = gf.Mul(r[j], inv);
        return r;
    }
}
