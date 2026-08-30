namespace QrShard.Tests;

public class ImageSequenceTests
{
    [Fact]
    public void Enumerate_MixedExtensions_ReturnsSupportedStillsInOrdinalIgnoreCaseNameOrder()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("c.PNG"), "x");
        File.WriteAllText(tmp.File("a.jpg"), "x");
        File.WriteAllText(tmp.File("b.bmp"), "x");
        File.WriteAllText(tmp.File("z.gif"), "x");
        File.WriteAllText(tmp.File("m.jpeg"), "x");
        File.WriteAllText(tmp.File("skip.txt"), "x");
        File.WriteAllText(tmp.File("skip.tif"), "x");

        Assert.Equal(
            ["a.jpg", "b.bmp", "c.PNG", "m.jpeg", "z.gif"],
            Display.ImageSequence.Enumerate(tmp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Enumerate_EmptyFolder_ReturnsEmptyList()
    {
        using var tmp = new TempDir();
        Assert.Empty(Display.ImageSequence.Enumerate(tmp.Path));
    }
}
