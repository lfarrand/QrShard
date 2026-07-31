namespace QrShard.Tests;

/// <summary>
/// The all-flip branch assumed EVERY ambiguous cell was misread. When most but not all were,
/// flipping the ones already right introduced fresh errors, and if that overshoot passed
/// t = parity/2 the codeword was lost outright. Un-flipping a subset only has to get back UNDER
/// the bound, not undo every mistake, so a search over the first six flagged positions rescues it.
///
/// Gated at parity 16 because below that the extra shots at the bound cost far more than they buy.
/// </summary>
public class ChaseUnflipTests
{
    /// <summary>
    /// A codeword in the regime where all-flip is the only stage left AND it fails: more flagged
    /// symbols than the erasure margin allows, with `correctlyClassified` of them read correctly
    /// (their runner-up is a decoy) so that flipping everything overshoots t.
    /// </summary>
    private static (byte[] Stream, byte[] Recv, byte[] Second, bool[] Suspects) Build(
        int parity, int correctlyClassified, int misread, Random rnd)
    {
        int dataLen = Fec.DataLength(parity);
        var stream = new byte[dataLen];
        rnd.NextBytes(stream);
        var truth = new Fec().Protect(stream, parity, 1);
        var recv = (byte[])truth.Clone();
        var second = (byte[])truth.Clone();
        var suspects = new bool[truth.Length];

        var picked = new List<int>();
        var seen = new HashSet<int>();
        while (picked.Count < correctlyClassified + misread)
        {
            int p = rnd.Next(Fec.CodewordLength);
            if (seen.Add(p)) picked.Add(p);
        }
        picked.Sort(); // the first six by index are what the search will aim at
        for (int i = 0; i < picked.Count; i++)
        {
            int p = picked[i];
            suspects[p] = true;
            if (i < correctlyClassified)
                second[p] = (byte)(truth[p] ^ (1 + rnd.Next(255))); // read right, runner-up is a decoy
            else
            {
                recv[p] = (byte)(truth[p] ^ (1 + rnd.Next(255)));   // misread, runner-up is the truth
                second[p] = truth[p];
            }
        }
        return (stream, recv, second, suspects);
    }

    private static (int Right, int Wrong) Sweep(int parity, int seed, int n)
    {
        var fec = new Fec();
        var rnd = new Random(seed);
        int dataLen = Fec.DataLength(parity), right = 0, wrong = 0;
        for (int i = 0; i < n; i++)
        {
            int correct = parity / 2 + 1 + rnd.Next(3);
            var (stream, recv, second, suspects) = Build(parity, correct, 6 + rnd.Next(6), rnd);
            var dest = new byte[dataLen];
            if (!fec.TryRecoverInto(recv, parity, 1, dest, out _, null, suspects, second))
                continue;
            if (dest.AsSpan().SequenceEqual(stream)) right++; else wrong++;
        }
        return (right, wrong);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void AnOvershootingAllFlipIsRescuedAtProtectiveParities(int parity)
    {
        var (right, wrong) = Sweep(parity, seed: 5150 + parity, n: 600);

        // Before the un-flip search this regime recovered NOTHING: all-flip overshoots t by
        // construction, and every earlier stage has already declined.
        Assert.True(right > 500, $"expected the un-flip search to rescue most of 600, got {right}");
        Assert.Equal(0, wrong);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void TheSearchIsNotOfferedWhereItWouldCostMoreThanItBuys(int parity)
    {
        // The gate, asserted as behaviour rather than trusted as a constant. At these parities the
        // extra trials return more wrong answers than right ones and multiply false acceptance of
        // past-capacity codewords 25-fold, so the branch must decline to run at all.
        //
        // The signature is CORRECT rescues, not acceptances. Low parity already miscorrects freely
        // without any help from Chase — at parity 4 the earlier stages accept most of this regime
        // and get essentially all of it wrong — so counting acceptances measures that pre-existing
        // noise rather than this gate. What the un-flip search would add is correct answers, 1636
        // of 3000 at parity 8, and those must not appear.
        var (right, wrong) = Sweep(parity, seed: 5150 + parity, n: 600);

        Assert.True(right < 30,
            $"parity {parity} should not be rescuing this regime, but recovered {right} of 600 correctly");
        Assert.True(wrong > right,
            $"parity {parity} is expected to miscorrect more than it recovers here ({wrong} wrong vs {right} right)");
    }

    [Fact]
    public void TheTrialBudgetIsUnchanged()
    {
        // The un-flip search reuses the exhaustive branch's budget rather than adding to it, so the
        // worst case a crafted image can force is still 63 Reed-Solomon decodes per codeword.
        Assert.Equal(6, GetMaxChasePositions());
        static int GetMaxChasePositions() =>
            (int)typeof(Fec).GetField("MaxChasePositions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetRawConstantValue()!;
    }
}
