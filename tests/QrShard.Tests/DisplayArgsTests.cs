namespace QrShard.Tests;

public class DisplayArgsTests
{
    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("500", 500)]
    [InlineData("1000", 1000)]
    public void TryParseFps_ValidInvariantText_ParsesValue(string text, double expected)
    {
        Assert.True(Display.DisplayPlayback.TryParseFps(text, out double fps));
        Assert.Equal(expected, fps);
    }

    [Fact]
    public void TryParseFps_NonNumeric_ReturnsFalse()
    {
        Assert.False(Display.DisplayPlayback.TryParseFps("fast", out _));
    }

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(5.0, true)]
    [InlineData(1000.0, true)]
    [InlineData(0.0, false)]
    [InlineData(1000.1, false)]
    [InlineData(-1.0, false)]
    public void ValidateFps_Range_MatchesRdcLimits(double fps, bool expected)
    {
        Assert.Equal(expected, Display.DisplayPlayback.ValidateFps(fps));
    }

    [Theory]
    [InlineData(5.0, false)]
    [InlineData(5.01, true)]
    [InlineData(500.0, true)]
    public void IsFlipbook_Threshold_GreaterThanFive(double fps, bool expected)
    {
        Assert.Equal(expected, Display.DisplayPlayback.IsFlipbook(fps));
    }

    [Theory]
    [InlineData("4", 4L * 1024 * 1024 * 1024)]
    [InlineData("4GB", 4L * 1024 * 1024 * 1024)]
    [InlineData("512MB", 512L * 1024 * 1024)]
    [InlineData(" 2gb ", 2L * 1024 * 1024 * 1024)]
    public void TryParseMemoryCap_ValidText_UsesGiBUnlessMbSuffix(string text, long expected)
    {
        Assert.True(Display.DisplayPlayback.TryParseMemoryCap(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("nope")]
    [InlineData("")]
    public void TryParseMemoryCap_NonPositiveOrJunk_ReturnsFalse(string text)
    {
        Assert.False(Display.DisplayPlayback.TryParseMemoryCap(text, out _));
    }

    [Fact]
    public void Format_TwoGib_UsesGigabyteLabel()
    {
        Assert.Equal("2 GB", Display.ByteSizeFormat.Format(Display.DisplayPlayback.DefaultMemoryCapBytes));
    }

    [Fact]
    public void Format_UnderOneGib_UsesMegabyteLabel()
    {
        Assert.Equal("512 MB", Display.ByteSizeFormat.Format(512L * 1024 * 1024));
    }
}
