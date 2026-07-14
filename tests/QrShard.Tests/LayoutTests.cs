using QrShard;

namespace QrShard.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(700, 700, 1, 1, 0)]
    [InlineData(700, 700, 3, 4, 16)]
    [InlineData(2160, 2160, 3, 4, 16)]
    [InlineData(2160, 2160, 2, 6, 32)]
    [InlineData(4096, 4096, 1, 8, 16)]
    [InlineData(3840, 2160, 1, 6, 16)]
    [InlineData(16384, 16384, 64, 8, 64)]
    public void Create_GeometryIsSelfConsistent(int width, int height, int cell, int bits, int parity)
    {
        var layout = Layout.Create(width, height, cell, bits, parity);

        Assert.Equal(layout.InnerW, 2 * layout.Gutter + layout.GridW * layout.CellPx);
        Assert.Equal(layout.InnerH, 2 * layout.Gutter + 4 * layout.MetaH + layout.GridH * layout.CellPx);
        Assert.Equal(layout.Width, layout.InnerW + 2 * Layout.Border);
        Assert.Equal(layout.Height, layout.InnerH + 2 * Layout.Border);
        Assert.Equal((long)layout.GridW * layout.GridH * bits, layout.TotalBits);
        Assert.True(layout.Width <= width);
        Assert.True(layout.Height <= height);
        Assert.True(layout.UsableBytes > 0);
        Assert.True(layout.UsableBytes <= layout.TotalBytes);
    }

    [Fact]
    public void Create_DefaultConfig_HasExpectedCapacityBallpark()
    {
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        Assert.InRange(layout.UsableBytes, 190_000, 250_000);
    }

    [Fact]
    public void Create_EccOverhead_MatchesParityFraction()
    {
        var without = Layout.Create(2160, 2160, 3, 4, 0);
        var with = Layout.Create(2160, 2160, 3, 4, 16);
        Assert.Equal(without.TotalBytes, with.TotalBytes); // geometry unchanged
        Assert.Equal(with.CodewordCount * (long)Fec.DataLength(16), with.UsableBytes);
    }

    [Theory]
    [InlineData(699, 2160, 3, 4, 0)]    // width below min
    [InlineData(2160, 16385, 3, 4, 0)]  // height above max
    [InlineData(2160, 2160, 0, 4, 0)]   // cell too small
    [InlineData(2160, 2160, 65, 4, 0)]  // cell too large
    [InlineData(2160, 2160, 3, 0, 0)]   // bits too small
    [InlineData(2160, 2160, 3, 9, 0)]   // bits too large
    [InlineData(2160, 2160, 3, 4, -2)]  // negative parity
    [InlineData(2160, 2160, 3, 4, 66)]  // parity above max
    [InlineData(2160, 2160, 3, 4, 15)]  // odd parity
    public void Create_RejectsInvalidOptions(int width, int height, int cell, int bits, int parity) =>
        Assert.Throws<ArgumentException>(() => Layout.Create(width, height, cell, bits, parity));

    [Fact]
    public void Create_TooSmallForEcc_IsRejected()
    {
        // 19x19 grid of 1-bit cells is only ~45 bytes — less than one RS codeword.
        var ex = Assert.Throws<ArgumentException>(() => Layout.Create(700, 700, 32, 1, 16));
        Assert.Contains("error correction", ex.Message);
    }

    [Theory]
    [InlineData(700, 700, 1, 1, 0)]
    [InlineData(2160, 2160, 3, 4, 16)]
    [InlineData(3840, 2160, 1, 6, 32)]
    [InlineData(4096, 4096, 1, 8, 64)]
    [InlineData(16384, 16384, 64, 8, 2)]
    public void Metadata_PackUnpack_RoundTrips(int width, int height, int cell, int bits, int parity)
    {
        var layout = Layout.Create(width, height, cell, bits, parity);
        var restored = Layout.UnpackMetadata(ToModules(layout.PackMetadata()));

        Assert.NotNull(restored);
        Assert.Equal(layout.BitsPerCell, restored.BitsPerCell);
        Assert.Equal(layout.GridW, restored.GridW);
        Assert.Equal(layout.GridH, restored.GridH);
        Assert.Equal(layout.CellPx, restored.CellPx);
        Assert.Equal(layout.MetaH, restored.MetaH);
        Assert.Equal(layout.InnerW, restored.InnerW);
        Assert.Equal(layout.InnerH, restored.InnerH);
        Assert.Equal(layout.EccParity, restored.EccParity);
    }

    [Fact]
    public void Metadata_AnySingleBitFlip_IsRejected()
    {
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        bool[] modules = ToModules(layout.PackMetadata());

        // Every one of the 128 bits is protected (112 data + 16 CRC).
        for (int i = 0; i < Layout.MetaModuleCount; i++)
        {
            modules[i] = !modules[i];
            Assert.Null(Layout.UnpackMetadata(modules));
            modules[i] = !modules[i];
        }
        Assert.NotNull(Layout.UnpackMetadata(modules));
    }

    [Fact]
    public void Metadata_WrongModuleCount_IsRejected() =>
        Assert.Null(Layout.UnpackMetadata(new bool[64]));

    [Fact]
    public void Metadata_AllZero_IsRejected() =>
        Assert.Null(Layout.UnpackMetadata(new bool[Layout.MetaModuleCount]));

    [Fact]
    public void EstimateMetaH_MatchesEncodedMetaH_ForSupportedResolutions()
    {
        // The decoder locates the metadata strip using this estimate before it can read the
        // exact value, so the estimate must stay within ~1px of the encoded strip height.
        for (int res = Layout.MinResolution; res <= Layout.MaxResolution; res += 997)
        {
            var layout = Layout.Create(res, res, 3, 4, 0);
            double estimate = Layout.EstimateMetaH(layout.InnerW);
            Assert.True(Math.Abs(estimate - layout.MetaH) <= 1.5,
                $"res={res}: estimate {estimate} vs encoded {layout.MetaH}");
        }
    }

    private static bool[] ToModules(byte[] packed)
    {
        var modules = new bool[Layout.MetaModuleCount];
        for (int i = 0; i < modules.Length; i++)
            modules[i] = (packed[i >> 3] & (0x80 >> (i & 7))) != 0;
        return modules;
    }
}
