using QrShard;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

public class PaletteTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Build_ReturnsCorrectColorCount(int bits) =>
        Assert.Equal(1 << bits, new Palette().Build(bits).Length);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Build_AllColorsAreDistinct(int bits)
    {
        var colors = new Palette().Build(bits);
        Assert.Equal(colors.Length, colors.Distinct().Count());
    }

    [Fact]
    public void Build_OneBit_IsBlackAndWhite()
    {
        var colors = new Palette().Build(1);
        Assert.Equal(new Rgb24(0, 0, 0), colors[0]);
        Assert.Equal(new Rgb24(255, 255, 255), colors[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void Build_RejectsOutOfRangeBits(int bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Palette().Build(bits));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Nearest_ExactColor_ReturnsItsOwnIndex(int bits)
    {
        var colors = new Palette().Build(bits);
        for (int i = 0; i < colors.Length; i++)
            Assert.Equal(i, new Palette().Nearest(colors, colors[i].R, colors[i].G, colors[i].B));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Nearest_TossesSmallPerturbations(int bits)
    {
        // Perturbations well below half the channel spacing must never change classification.
        var colors = new Palette().Build(bits);
        for (int i = 0; i < colors.Length; i++)
        {
            int r = Math.Clamp(colors[i].R + 12, 0, 255);
            int g = Math.Clamp(colors[i].G - 12, 0, 255);
            int b = Math.Clamp(colors[i].B + 12, 0, 255);
            Assert.Equal(i, new Palette().Nearest(colors, r, g, b));
        }
    }

    [Fact]
    public void Build_ChannelSpacing_IsWideEnoughForClassification()
    {
        // For every supported density, distinct palette entries must differ by a comfortable
        // margin in at least one channel (>= 32 levels for 8-bit, wider for sparser palettes).
        for (int bits = 1; bits <= 8; bits++)
        {
            var colors = new Palette().Build(bits);
            int minDistSq = int.MaxValue;
            for (int i = 0; i < colors.Length; i++)
            {
                for (int j = i + 1; j < colors.Length; j++)
                {
                    int dr = colors[i].R - colors[j].R;
                    int dg = colors[i].G - colors[j].G;
                    int db = colors[i].B - colors[j].B;
                    minDistSq = Math.Min(minDistSq, dr * dr + dg * dg + db * db);
                }
            }
            Assert.True(minDistSq >= 32 * 32, $"bits={bits}: min palette distance^2 {minDistSq} too small");
        }
    }
}
