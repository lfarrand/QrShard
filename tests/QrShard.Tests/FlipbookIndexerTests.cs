namespace QrShard.Tests;

public class FlipbookIndexerTests
{
    [Fact]
    public void Advance_OnSchedule_StepsOneFrame()
    {
        Assert.Equal(3, Display.FlipbookIndexer.Advance(timeIndex: 3, lastRawIndex: 2));
    }

    [Fact]
    public void Advance_SlightlyBehind_DoesNotSkip()
    {
        Assert.Equal(6, Display.FlipbookIndexer.Advance(timeIndex: 10, lastRawIndex: 5));
    }

    [Fact]
    public void Advance_BehindMoreThanCatchUp_JumpsToTimeIndex()
    {
        Assert.Equal(20, Display.FlipbookIndexer.Advance(timeIndex: 20, lastRawIndex: 5));
    }

    [Fact]
    public void Advance_NegativeTimeIndex_ClampsToZero()
    {
        Assert.Equal(0, Display.FlipbookIndexer.Advance(timeIndex: -3, lastRawIndex: -1));
    }

    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(11, 10, true)]
    public void OncePastLast_AtOrBeyondCount_IsTrue(long rawIndex, int count, bool expected)
    {
        Assert.Equal(expected, Display.FlipbookIndexer.OncePastLast(rawIndex, count));
    }
}
