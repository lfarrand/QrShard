namespace QrShard.Tests;

/// <summary>
/// Chase reported the INNER decoder's error count, but the inner decoder is handed the
/// already-spliced trial codeword — so the symbols Chase substituted from the second-choice
/// stream, which are exactly the corrections it made, were never counted. In the all-flip branch
/// the splice frequently makes the word syndrome-clean, and a codeword rescued from dozens of
/// misread symbols reported none at all.
///
/// Not cosmetic: CalibrationRunner divides CorrectedBytes by the parity budget to score ECC
/// utilisation and recommends a DENSER setting when it comes back low, so a capture rescued at the
/// last resort scored 0% and was told to push further. The quality heatmap read the same zero.
/// </summary>
public class ChaseCorrectionCountTests
{
    private const int Parity = 16, CwCount = 1;

    /// <summary>
    /// Damages <paramref name="damaged"/> symbols and flags every one, with the second-choice
    /// stream holding the true value — so whichever stage takes the job restores the codeword
    /// exactly and the honest correction count is always <paramref name="damaged"/>.
    /// </summary>
    private static (bool Ok, int Corrected, int Heatmap) RecoverWith(int damaged)
    {
        var fec = new Fec();
        int dataLen = Fec.DataLength(Parity);
        var stream = TestData.Random(dataLen, seed: 3);
        var buffer = fec.Protect(stream, Parity, CwCount);
        var second = (byte[])buffer.Clone(); // the true value at every flagged position
        var suspects = new bool[buffer.Length];
        for (int k = 0; k < damaged; k++)
        {
            int p = 3 + k * 4;
            buffer[p] ^= 0x2D;
            suspects[p] = true;
        }

        var cwErrors = new int[CwCount];
        var dest = new byte[dataLen];
        bool ok = fec.TryRecoverInto(buffer, Parity, CwCount, dest, out int corrected, cwErrors, suspects, second);
        Assert.Equal(stream, dest);
        return (ok, corrected, cwErrors[0]);
    }

    [Theory]
    [InlineData(3)]   // errors-only
    [InlineData(6)]   // Chase, exhaustive subset branch
    [InlineData(10)]  // erasure retry (10 flags still inside parity - VerificationMargin)
    [InlineData(25)]  // Chase, all-flip branch — reported 0 before the fix
    [InlineData(60)]  // Chase, all-flip branch — reported 0 before the fix
    public void EverySymbolTheDecodeChangedIsCounted(int damaged)
    {
        var (ok, corrected, heatmap) = RecoverWith(damaged);

        Assert.True(ok, $"{damaged} damaged symbols should still recover at parity {Parity}");
        Assert.Equal(damaged, corrected);
        Assert.Equal(damaged, heatmap);
    }

    [Fact]
    public void ARescuedCaptureIsNeverScoredAsUsingNoErrorCorrection()
    {
        // The consequence, stated as the property that actually matters. A codeword that only the
        // last-resort branch could save must not look identical to one that needed nothing — that
        // equivalence is what made calibrate recommend a denser setting to a capture already at
        // the edge of what it could recover.
        var untouched = RecoverWith(0);
        var rescued = RecoverWith(60);

        Assert.Equal(0, untouched.Corrected);
        Assert.True(rescued.Corrected > 0, "a Chase-rescued codeword reported zero corrections");
        Assert.NotEqual(untouched.Heatmap, rescued.Heatmap);
    }
}
