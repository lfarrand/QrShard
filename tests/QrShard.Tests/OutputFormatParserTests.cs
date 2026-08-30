namespace QrShard.Tests;

public class OutputFormatParserTests
{
    [Theory]
    [InlineData(null, "png")]
    [InlineData("PNG", "png")]
    [InlineData(" tiff ", "tiff")]
    [InlineData("TIF", "tiff")]
    [InlineData("bmp", "bmp")]
    public void TryNormalize_KnownAliases_ReturnsCanonicalName(string? text, string expected)
    {
        Assert.Equal(expected, Recorder.OutputFormatParser.TryNormalize(text));
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("jpg")]
    [InlineData("webp")]
    public void TryNormalize_Unsupported_ReturnsNull(string text)
    {
        Assert.Null(Recorder.OutputFormatParser.TryNormalize(text));
    }
}
