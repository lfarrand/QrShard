namespace QrShard;

/// <summary>
/// The v2 interleave: a seeded pseudo-random permutation of the ECC-protected byte range,
/// applied AROUND the classic modular interleave (encode scatters the classic buffer through
/// it; decode gathers back before the SIMD/erasure machinery, which therefore runs unchanged).
///
/// Why: the classic map (byte k → codeword k mod cwCount) spreads HORIZONTAL damage perfectly,
/// but a vertical blob damages bytes at a fixed stride, and when that stride shares a large
/// factor with cwCount the damage concentrates on a few codewords instead of spreading. A
/// Fisher-Yates permutation seeded only by the length (so both sides derive it identically,
/// with nothing extra carried in the image) destroys every such arithmetic structure.
/// </summary>
internal sealed class Interleaver2
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<int, Lazy<int[]>> _inFlight = [];
    private int _strongLength = -1;
    private int[]? _strongPermutation;
    private int _weakLength = -1;
    private WeakReference<int[]>? _weakPermutation;
    private int _permutationBuilds;

    /// <summary>
    /// A normal Max4K permutation is about 21 MB. Cache only the most recently used one and only
    /// below a byte cap: geometry is attacker-controlled, so an entry-count cap retained more than
    /// a gigabyte across 64 realistic sizes. A real transfer repeats one length for every image,
    /// which makes a one-entry cache the useful policy as well as the bounded one.
    /// </summary>
    private const int MaxCachedBytes = 32 * 1024 * 1024;

    internal int CachedBytes
    {
        get
        {
            lock (_cacheLock)
                return (_strongPermutation?.Length ?? 0) * sizeof(int);
        }
    }

    internal int PermutationBuilds => Volatile.Read(ref _permutationBuilds);

    /// <summary>π for a protected region of <paramref name="length"/> bytes: dest[π[i]] = classic[i].</summary>
    public int[] Permutation(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Lazy<int[]> pending;
        lock (_cacheLock)
        {
            if (_strongLength == length)
                return _strongPermutation!;
            if (_weakLength == length && _weakPermutation is not null &&
                _weakPermutation.TryGetTarget(out int[]? weak))
                return weak;

            // A Lazy per active length is a single-flight gate: every concurrent encoder worker
            // observes the same permutation, including geometries above the persistent-cache cap.
            // Entries live only while a build is active, so arbitrary lengths cannot accumulate
            // as retained cache state.
            if (!_inFlight.TryGetValue(length, out pending!))
            {
                pending = new Lazy<int[]>(() =>
                {
                    Interlocked.Increment(ref _permutationBuilds);
                    return BuildPermutation(length);
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                _inFlight.Add(length, pending);
            }
        }

        int[] built;
        try
        {
            built = pending.Value;
        }
        catch
        {
            lock (_cacheLock)
                if (_inFlight.TryGetValue(length, out Lazy<int[]>? active) && ReferenceEquals(active, pending))
                    _inFlight.Remove(length);
            throw;
        }

        lock (_cacheLock)
        {
            if (_inFlight.TryGetValue(length, out Lazy<int[]>? active) && ReferenceEquals(active, pending))
                _inFlight.Remove(length);

            if ((long)length * sizeof(int) <= MaxCachedBytes)
            {
                _strongLength = length;
                _strongPermutation = built;
            }
            else
            {
                // Keep a weak hand-off for large operation-scoped permutations. All active
                // workers hold the array strongly, while an idle singleton does not pin a
                // hundreds-of-megabytes allocation after the operation ends.
                _weakLength = length;
                _weakPermutation = new WeakReference<int[]>(built);
            }
            return built;
        }
    }

    private static int[] BuildPermutation(int length)
    {
        var perm = new int[length];
        for (int i = 0; i < length; i++)
            perm[i] = i;

        // SplitMix64 stream seeded by the length — deterministic on both sides by construction.
        ulong state = 0x9E3779B97F4A7C15UL ^ (ulong)(uint)length;
        for (int i = length - 1; i > 0; i--)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            int k = (int)(z % (ulong)(i + 1));
            (perm[i], perm[k]) = (perm[k], perm[i]);
        }
        return perm;
    }

    /// <summary>Encode side: classic-interleaved bytes scattered into cell-stream order.</summary>
    public void Scatter(byte[] classic, byte[] dest, int protectedLength)
        => Scatter(classic, dest, protectedLength, Permutation(protectedLength));

    /// <summary>Scatter using an operation-scoped permutation already acquired by the caller.</summary>
    public static void Scatter(byte[] classic, byte[] dest, int protectedLength, int[] permutation)
    {
        if (permutation.Length != protectedLength)
            throw new ArgumentException("The interleave permutation does not match the protected length.", nameof(permutation));
        for (int i = 0; i < protectedLength; i++)
            dest[permutation[i]] = classic[i];
    }

    /// <summary>Decode side: sampled cell bytes gathered back into classic interleave order.</summary>
    public void Gather(byte[] cells, byte[] classicDest, int protectedLength)
    {
        int[] perm = Permutation(protectedLength);
        for (int i = 0; i < protectedLength; i++)
            classicDest[i] = cells[perm[i]];
    }

    public void GatherFlags(bool[] cellFlags, bool[] classicDest, int protectedLength)
    {
        int[] perm = Permutation(protectedLength);
        for (int i = 0; i < protectedLength; i++)
            classicDest[i] = cellFlags[perm[i]];
    }

    /// <summary>
    /// Decode all confidence streams with one permutation. Large valid geometries intentionally
    /// bypass the persistent cache; independently calling Gather/GatherFlags for cells, flags and
    /// second choices would otherwise allocate and shuffle the same >32 MiB int array three times
    /// for every image.
    /// </summary>
    public void GatherStreams(byte[] cells, byte[] classicCells,
        bool[]? cellFlags, bool[]? classicFlags,
        byte[]? secondChoices, byte[]? classicSecondChoices,
        int protectedLength)
    {
        int[] perm = Permutation(protectedLength);
        for (int i = 0; i < protectedLength; i++)
        {
            int source = perm[i];
            classicCells[i] = cells[source];
            if (cellFlags is not null && classicFlags is not null)
                classicFlags[i] = cellFlags[source];
            if (secondChoices is not null && classicSecondChoices is not null)
                classicSecondChoices[i] = secondChoices[source];
        }
    }
}
