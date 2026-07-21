using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<int, int[]> _cache = new();

    /// <summary>π for a protected region of <paramref name="length"/> bytes: dest[π[i]] = classic[i].</summary>
    public int[] Permutation(int length) => _cache.GetOrAdd(length, BuildPermutation);

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
    {
        int[] perm = Permutation(protectedLength);
        for (int i = 0; i < protectedLength; i++)
            dest[perm[i]] = classic[i];
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
}
