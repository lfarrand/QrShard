using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The one-way invariant that matters for an integrity tool: a decode may fail, but it must never
/// succeed with bytes other than the ones transmitted.
///
/// Reed-Solomon cannot detect every miscorrection beyond its bound — that is inherent, and the
/// payload CRC-32 and whole-file SHA-256 are the backstops. What it must not do is spend every
/// syndrome on correction, because then a wrong answer is *certain* to pass verification rather
/// than merely possible: with f == nsym erasures the solve is exactly determined, so an assignment
/// making every syndrome vanish always exists, and the decoder returns it with a clean bill of
/// health every single time.
/// </summary>
public class ErasureMiscorrectionTests
{
    private static byte[] MakeCodeword(int dataLen, int nsym, int seed)
    {
        var rs = new ReedSolomon();
        var cw = new byte[dataLen + nsym];
        new Random(seed).NextBytes(cw.AsSpan(0, dataLen));
        rs.Encode(cw.AsSpan(0, dataLen), cw.AsSpan(dataLen));
        return cw;
    }

    /// <summary>Flags <paramref name="f"/> positions, damaging half of them plus one symbol
    /// OUTSIDE the flagged set — the case the erasure budget cannot legitimately explain.</summary>
    private static (byte[] Clean, byte[] Damaged, int[] Erasures) BuildOverBudget(int nsym, int f, int seed)
    {
        byte[] clean = MakeCodeword(100, nsym, seed);
        var damaged = (byte[])clean.Clone();
        var rng = new Random(seed * 31 + 7);
        var positions = Enumerable.Range(0, clean.Length).OrderBy(_ => rng.Next()).ToArray();

        int[] erasures = positions.Take(f).ToArray();
        foreach (int p in erasures.Take(Math.Max(1, f / 2)))
            damaged[p] ^= (byte)rng.Next(1, 256);
        damaged[positions[f]] ^= (byte)rng.Next(1, 256); // the unflagged error
        return (clean, damaged, erasures);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void FullBudgetErasures_AreRefused_RatherThanSilentlyMiscorrected(int nsym)
    {
        var rs = new ReedSolomon();
        for (int seed = 0; seed < 200; seed++)
        {
            var (clean, damaged, erasures) = BuildOverBudget(nsym, nsym, seed);
            bool ok = rs.TryDecodeWithErasures(damaged, nsym, erasures, out _);
            Assert.True(!ok || damaged.AsSpan().SequenceEqual(clean),
                $"nsym={nsym} seed={seed}: reported success with a codeword that is valid but " +
                "not the transmitted one");
        }
    }

    /// <summary>
    /// The invariant across the whole erasure range, not just the fully-determined corner: for
    /// every f, a success must mean the right bytes.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void SuccessfulErasureDecode_AlwaysEqualsTheTransmittedCodeword(int nsym)
    {
        var rs = new ReedSolomon();
        int accepted = 0;
        for (int f = 1; f <= nsym; f++)
        {
            for (int seed = 0; seed < 40; seed++)
            {
                var (clean, damaged, erasures) = BuildOverBudget(nsym, f, seed);
                if (!rs.TryDecodeWithErasures(damaged, nsym, erasures, out _))
                    continue;
                accepted++;
                Assert.True(damaged.AsSpan().SequenceEqual(clean),
                    $"nsym={nsym} f={f} seed={seed}: silent miscorrection");
            }
        }
        // Sanity: the sweep must actually exercise the success path, or it proves nothing.
        Assert.True(accepted > 0, "no decode succeeded — the corpus never reached the accept path");
    }

    [Fact]
    public void TheDocumentedErasureCapacity_StillDecodes()
    {
        // The README promises "up to ~14" flagged bytes per block at the default parity of 16.
        // The verification margin must not have quietly eaten that.
        const int nsym = 16;
        var rs = new ReedSolomon();
        byte[] clean = MakeCodeword(100, nsym, 11);
        var damaged = (byte[])clean.Clone();
        int[] flagged = [3, 9, 17, 20, 33, 41, 47, 58, 66, 71, 84, 90, 101, 110]; // 14
        foreach (int p in flagged)
            damaged[p] ^= 0x5A;

        Assert.True(rs.TryDecodeWithErasures(damaged, nsym, flagged, out _));
        Assert.Equal(clean, damaged);
    }

    [Fact]
    public void OverFlaggedCodeword_FallsThroughToChase_RatherThanReportingSuccess()
    {
        // End-to-end at the Fec level: the classifier flags more symbols than the budget allows
        // (a blurry capture), so erasure retry must decline instead of manufacturing an answer.
        const int parity = 16, cwCount = 1;
        var fec = new Fec();
        int dataLen = Fec.DataLength(parity);
        var stream = new byte[dataLen];
        new Random(5).NextBytes(stream);

        var buffer = new byte[Fec.CodewordLength * cwCount];
        fec.ProtectInto(stream, stream.Length, parity, cwCount, buffer);

        var suspects = new bool[buffer.Length];
        var rng = new Random(9);
        var positions = Enumerable.Range(0, Fec.CodewordLength).OrderBy(_ => rng.Next()).ToArray();
        foreach (int i in positions.Take(parity))          // flag the full budget
            suspects[i * cwCount] = true;
        foreach (int i in positions.Take(parity / 2))      // damage half of them
            buffer[i * cwCount] ^= (byte)rng.Next(1, 256);
        buffer[positions[parity] * cwCount] ^= 0x33;        // plus an unflagged error

        var dest = new byte[stream.Length];
        bool ok = fec.TryRecoverInto(buffer, parity, cwCount, dest, out _, null, suspects, null);
        Assert.True(!ok || dest.AsSpan().SequenceEqual(stream),
            "TryRecoverInto reported success on an over-flagged codeword with wrong bytes");
    }
}
