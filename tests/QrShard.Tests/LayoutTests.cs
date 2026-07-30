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

    /// <summary>
    /// This used to assert the opposite — that every single bit flip is REJECTED — which was an
    /// accurate description of version 2 and the whole reason for version 4. The strip is read
    /// before the data grid's ECC is consulted, so one flipped module lost an image whose payload
    /// was intact and fully protected. Now the flip is repaired and the image decodes.
    /// </summary>
    [Fact]
    public void Metadata_AnySingleBitFlip_IsCorrected()
    {
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        bool[] modules = ToModules(layout.PackMetadata());

        for (int i = 0; i < Layout.MetaModuleCount; i++)
        {
            modules[i] = !modules[i];
            var restored = Layout.UnpackMetadata(modules);
            Assert.True(restored is not null, $"module {i} flipped: strip should have been repaired");
            Assert.Equal(layout.GridW, restored!.GridW);
            Assert.Equal(layout.GridH, restored.GridH);
            Assert.Equal(layout.EccParity, restored.EccParity);
            modules[i] = !modules[i];
        }
        Assert.NotNull(Layout.UnpackMetadata(modules));
    }

    /// <summary>
    /// The correction budget, from both sides. Five parity symbols correct two symbol errors, so a
    /// burst confined to two bytes is repaired however many bits inside them are wrong — which is
    /// the shape that matters, since the damage this exists for is a mark rather than scattered
    /// noise. Past that it must FAIL rather than return a plausible wrong geometry: a miscorrected
    /// strip would point the decoder at the wrong grid, and the CRC is retained underneath the RS
    /// precisely to catch that.
    /// </summary>
    [Fact]
    public void Metadata_BurstWithinTwoSymbols_IsCorrected_AndBeyondIsRejected()
    {
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        byte[] clean = layout.PackMetadata();

        // A 16-module burst aligned to two bytes: every bit of symbols 3 and 4 inverted.
        var twoSymbols = (byte[])clean.Clone();
        twoSymbols[3] ^= 0xFF;
        twoSymbols[4] ^= 0xFF;
        var repaired = Layout.UnpackMetadata(ToModules(twoSymbols));
        Assert.True(repaired is not null, "a burst inside two symbols should be repaired");
        Assert.Equal(layout.GridW, repaired!.GridW);
        Assert.Equal(layout.MetaH, repaired.MetaH);

        // Three corrupted symbols is past the bound and must not produce a Layout.
        var threeSymbols = (byte[])clean.Clone();
        threeSymbols[3] ^= 0xFF;
        threeSymbols[4] ^= 0xFF;
        threeSymbols[5] ^= 0xFF;
        Assert.Null(Layout.UnpackMetadata(ToModules(threeSymbols)));
    }

    [Fact]
    public void Metadata_LegacyVersion2Strip_StillDecodes()
    {
        // Forward compatibility is the point of the version nibble, but backward compatibility is
        // what keeps shards already in the wild readable. A v2 strip carries innerW/innerH
        // explicitly where v4 derives them, so this also pins that the two paths agree.
        var layout = Layout.Create(2160, 2160, 3, 4, 16);
        var bits = new BitWriter();
        bits.Write(Layout.MetaMagic, 8);
        bits.Write(Layout.MetaVersion, 4);
        bits.Write((uint)layout.BitsPerCell, 4);
        bits.Write((uint)layout.GridW, 16);
        bits.Write((uint)layout.GridH, 16);
        bits.Write((uint)layout.CellPx, 8);
        bits.Write((uint)layout.MetaH, 16);
        bits.Write((uint)layout.InnerW, 16);
        bits.Write((uint)layout.InnerH, 16);
        bits.Write((uint)layout.EccParity, 8);
        byte[] payload = bits.ToArray();
        bits.Write(new Crc().Crc16Ccitt(payload), 16);

        var restored = Layout.UnpackMetadata(ToModules(bits.ToArray()));
        Assert.NotNull(restored);
        Assert.Equal(layout.GridW, restored!.GridW);
        Assert.Equal(layout.GridH, restored.GridH);
        Assert.Equal(layout.InnerW, restored.InnerW);
        Assert.Equal(layout.InnerH, restored.InnerH);
        Assert.Equal(layout.EccParity, restored.EccParity);
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
