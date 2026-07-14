using System.Text;
using QrShard;

namespace QrShard.Tests;

public class CrcTests
{
    // "123456789" is the standard check-value input for CRC catalogues.
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void Crc32_MatchesIeeeCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc.Crc32(Check));

    [Fact]
    public void Crc16Ccitt_MatchesCcittFalseCheckValue() =>
        Assert.Equal((ushort)0x29B1, Crc.Crc16Ccitt(Check));

    [Fact]
    public void Crc32_EmptyInput_IsZero() =>
        Assert.Equal(0u, Crc.Crc32([]));

    [Fact]
    public void Crc32_DetectsSingleBitFlip()
    {
        var data = new byte[256];
        new Random(1).NextBytes(data);
        uint original = Crc.Crc32(data);
        for (int i = 0; i < data.Length; i += 37)
        {
            data[i] ^= 0x10;
            Assert.NotEqual(original, Crc.Crc32(data));
            data[i] ^= 0x10;
        }
        Assert.Equal(original, Crc.Crc32(data));
    }

    [Fact]
    public void Crc16_DetectsSingleBitFlip()
    {
        var data = new byte[64];
        new Random(2).NextBytes(data);
        ushort original = Crc.Crc16Ccitt(data);
        for (int i = 0; i < data.Length; i += 7)
        {
            data[i] ^= 0x01;
            Assert.NotEqual(original, Crc.Crc16Ccitt(data));
            data[i] ^= 0x01;
        }
    }

    [Fact]
    public void Crc32_IsDeterministic()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        Assert.Equal(Crc.Crc32(data), Crc.Crc32(data));
    }
}
