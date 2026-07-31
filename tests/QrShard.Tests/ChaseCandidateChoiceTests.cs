namespace QrShard.Tests;

/// <summary>
/// Chase returned the first trial pattern that verified, in ascending binary-mask order. Several
/// patterns commonly produce valid codewords past the errors-only bound, and mask order ranks them
/// by nothing meaningful, so the answer was an artefact of enumeration.
///
/// It now takes the candidate Reed-Solomon spent the FEWEST corrections on. Not the one closest to
/// the received word: that reading conflates the splices, which are hypotheses the decoder chose,
/// with the corrections, which are evidence of error — and scoring on it measured strictly worse
/// than taking the first hit.
/// </summary>
public class ChaseCandidateChoiceTests
{
    /// <summary>
    /// Six flagged positions with a mixed correct/misread split, plus enough unflagged errors that
    /// errors-only and the erasure retry both decline — the regime where the exhaustive Chase
    /// branch is the stage that decides the answer.
    /// </summary>
    private static (int Right, int Wrong, int Ran) Sweep(int parity, int n, int seed)
    {
        var fec = new Fec();
        var rnd = new Random(seed);
        int dataLen = Fec.DataLength(parity), t = parity / 2;
        int right = 0, wrong = 0, ran = 0;

        for (int i = 0; i < n; i++)
        {
            var stream = new byte[dataLen];
            rnd.NextBytes(stream);
            var truth = fec.Protect(stream, parity, 1);
            var recv = (byte[])truth.Clone();
            var second = (byte[])truth.Clone();
            var suspects = new bool[truth.Length];

            var seen = new HashSet<int>();
            var pos = new List<int>();
            while (pos.Count < 6) { int p = rnd.Next(Fec.CodewordLength); if (seen.Add(p)) pos.Add(p); }
            foreach (int p in pos)
            {
                suspects[p] = true;
                if (rnd.NextDouble() < 0.7) { recv[p] = (byte)(truth[p] ^ (1 + rnd.Next(255))); second[p] = truth[p]; }
                else second[p] = (byte)(truth[p] ^ (1 + rnd.Next(255)));
            }
            for (int e = 0; e < t - 2 + rnd.Next(4); e++)
            {
                int q = rnd.Next(Fec.CodewordLength);
                if (seen.Add(q)) recv[q] = (byte)(truth[q] ^ (1 + rnd.Next(255)));
            }

            var dest = new byte[dataLen];
            if (!fec.TryRecoverInto(recv, parity, 1, dest, out _, null, suspects, second)) continue;
            ran++;
            if (dest.AsSpan().SequenceEqual(stream)) right++; else wrong++;
        }
        return (right, wrong, ran);
    }

    [Theory]
    [InlineData(8, 0.55)]
    [InlineData(12, 0.93)]
    [InlineData(16, 0.98)]
    public void TheChosenCandidateIsRightFarMoreOftenThanTheFirstOneToVerify(int parity, double floor)
    {
        // First-match measured 2532/5885 correct at parity 8; choosing on correction weight took
        // that to 3544 with nothing broken anywhere. These floors sit below the measured rates but
        // well above what first-match achieved, so a regression to enumeration order fails them.
        var (right, wrong, ran) = Sweep(parity, n: 2000, seed: 4242 + parity);

        Assert.True(ran > 1000, $"the Chase branch should be deciding most of these, ran on {ran}");
        double rate = (double)right / ran;
        Assert.True(rate >= floor, $"parity {parity}: only {right}/{ran} = {rate:P1} correct ({wrong} wrong)");
    }

    [Fact]
    public void TheChoiceIsDeterministic()
    {
        // A tie broken by iteration order would make the same capture decode differently between
        // runs or worker counts — the worst kind of defect to diagnose. Ties keep the earliest
        // mask, so repeated runs over identical input must agree exactly.
        var a = Sweep(16, n: 400, seed: 31337);
        var b = Sweep(16, n: 400, seed: 31337);
        Assert.Equal(a, b);
    }
}
