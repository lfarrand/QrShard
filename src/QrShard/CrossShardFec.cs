namespace QrShard;

/// <summary>
/// Cross-shard (image-level) erasure coding: complements the per-image Reed-Solomon layer.
///
/// Per-image ECC (<see cref="Fec"/>) repairs *localized* damage inside a captured image. This layer
/// repairs *whole missing or unrecoverable images*: the data images of a stripe are extended with
/// parity images so that any <c>dataCount</c> of the <c>dataCount + parityCount</c> images in the
/// stripe reconstruct the originals. Losing, deleting, or failing to capture up to
/// <c>parityCount</c> images anywhere in a stripe costs nothing.
///
/// The generator is systematic with a Cauchy parity block, which is MDS: every square submatrix is
/// invertible, so *any* set of survivors of size <c>dataCount</c> is sufficient — there are no
/// unlucky loss patterns. A stripe holds at most 255 images total (the GF(2^8) limit); the encoder
/// partitions large files into independent stripes.
/// </summary>
internal static class CrossShardFec
{
    public const int MaxShardsPerStripe = 255;

    /// <summary>Cauchy parity row i, data column j: 1 / (x_i ^ y_j), with disjoint x/y domains.</summary>
    private static byte[][] ParityMatrix(int dataCount, int parityCount)
    {
        var m = new byte[parityCount][];
        for (int i = 0; i < parityCount; i++)
        {
            m[i] = new byte[dataCount];
            byte x = (byte)i;                     // parity domain: 0 .. parityCount-1
            for (int j = 0; j < dataCount; j++)
            {
                byte y = (byte)(parityCount + j); // data domain: parityCount .. parityCount+dataCount-1
                m[i][j] = Gf256.Inv((byte)(x ^ y));
            }
        }
        return m;
    }

    /// <summary>
    /// Computes <paramref name="parityCount"/> parity shards from equal-length data shards.
    /// All shards (data and parity) share <paramref name="shardLen"/> bytes.
    /// </summary>
    public static byte[][] Encode(IReadOnlyList<byte[]> dataShards, int parityCount, int shardLen)
    {
        int k = dataShards.Count;
        if (k < 1 || parityCount < 0)
            throw new ArgumentException("Invalid stripe dimensions.");
        if (k + parityCount > MaxShardsPerStripe)
            throw new ArgumentException($"A stripe cannot exceed {MaxShardsPerStripe} shards.");

        var matrix = ParityMatrix(k, parityCount);
        var parity = new byte[parityCount][];
        for (int i = 0; i < parityCount; i++)
            parity[i] = new byte[shardLen];

        System.Threading.Tasks.Parallel.For(0, parityCount, i =>
        {
            for (int j = 0; j < k; j++)
                Gf256.MulAdd(matrix[i][j], dataShards[j], parity[i]);
        });
        return parity;
    }

    /// <summary>
    /// Reconstructs all <paramref name="dataCount"/> data shards from any surviving shards.
    /// <paramref name="present"/>[s] is the bytes of shard s (data shards indexed 0..dataCount-1,
    /// parity shards dataCount..dataCount+parityCount-1) or null if that shard is missing.
    /// Returns false if fewer than <paramref name="dataCount"/> shards survive.
    /// </summary>
    public static bool TryReconstruct(byte[]?[] present, int dataCount, int parityCount, int shardLen, out byte[][] data)
    {
        data = [];
        int available = present.Count(p => p is not null);
        if (available < dataCount)
            return false;

        // Fast path: every data shard is present.
        if (Enumerable.Range(0, dataCount).All(i => present[i] is not null))
        {
            data = new byte[dataCount][];
            for (int i = 0; i < dataCount; i++)
                data[i] = present[i]!;
            return true;
        }

        // Combined generator rows: identity for data shards, Cauchy for parity shards.
        var parityMatrix = ParityMatrix(dataCount, parityCount);

        // Pick the first dataCount available shards and assemble their generator rows.
        var decodeMatrix = new byte[dataCount][];
        var rhs = new byte[dataCount][];
        int picked = 0;
        for (int s = 0; s < present.Length && picked < dataCount; s++)
        {
            if (present[s] is null)
                continue;
            var row = new byte[dataCount];
            if (s < dataCount)
                row[s] = 1;                        // data shard: identity row
            else
                parityMatrix[s - dataCount].CopyTo(row, 0); // parity shard: Cauchy row
            decodeMatrix[picked] = row;
            rhs[picked] = present[s]!;
            picked++;
        }

        if (!Gf256.Invert(decodeMatrix, dataCount))
            return false; // MDS guarantees this cannot happen; guard anyway

        // Recover each original data shard = inverted-row · surviving-shard-vector.
        var result = new byte[dataCount][];
        System.Threading.Tasks.Parallel.For(0, dataCount, i =>
        {
            var outShard = new byte[shardLen];
            for (int j = 0; j < dataCount; j++)
                Gf256.MulAdd(decodeMatrix[i][j], rhs[j], outShard);
            result[i] = outShard;
        });

        data = result;
        return true;
    }
}
