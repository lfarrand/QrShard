namespace QrShard.Tests;

public class CaptureSequenceTests
{
    [Fact]
    public void Next_EmptyFolder_StartsAtOne()
    {
        using var tmp = new TempDir();
        Assert.Equal(1, Recorder.CaptureSequence.Next(tmp.Path, "20260829", "png"));
    }

    [Fact]
    public void Next_ExistingDateFiles_ContinuesAfterHighest()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("20260829-1.png"), "x");
        File.WriteAllText(tmp.File("20260829-7.png"), "x");
        File.WriteAllText(tmp.File("20260829-3.png"), "x");
        File.WriteAllText(tmp.File("20260828-99.png"), "x");
        File.WriteAllText(tmp.File("20260829-2.bmp"), "x");

        Assert.Equal(8, Recorder.CaptureSequence.Next(tmp.Path, "20260829", "png"));
    }
}
