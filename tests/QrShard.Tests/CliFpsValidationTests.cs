using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

public class CliFpsValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void RecordingDecode_RejectsNonPositiveOrNonFiniteFps(string value)
    {
        using var tmp = new TempDir();
        string gif = tmp.File("two-frames.gif");
        using (var image = new Image<Rgb24>(2, 2))
        {
            using var second = new Image<Rgb24>(2, 2, new Rgb24(255, 255, 255));
            image.Frames.AddFrame(second.Frames.RootFrame);
            image.SaveAsGif(gif);
        }

        var error = new StringWriter();
        int code = new Cli().Run(["decode", gif, "--fps", value], new StringWriter(), error, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(0, code);
        Assert.Contains("--fps", error.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Receive_RejectsFpsOutsideItsDocumentedRangeBeforeOpeningACapture(string value)
    {
        var error = new StringWriter();
        int code = new Cli().Run(["receive", "--screen", "--fps", value], new StringWriter(), error, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(0, code);
        Assert.Contains("--fps", error.ToString());
    }
}
