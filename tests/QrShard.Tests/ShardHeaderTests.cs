using QrShard;

namespace QrShard.Tests;

public class ShardHeaderTests
{
    private static ShardHeader Sample(string fileName = "report.pdf", int index = 2, int count = 7,
        byte flags = ShardHeader.FlagCompressed, int stripeData = 0, int stripeParity = 0) => new()
    {
        FileId = 0x0123456789ABCDEF,
        Index = index,
        Count = count,
        PayloadLength = 12345,
        PayloadCrc32 = 0xDEADBEEF,
        TotalLength = 1_000_000,
        OriginalLength = 2_000_000,
        Flags = flags,
        Sha256 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        FileName = fileName,
        StripeData = stripeData,
        StripeParity = stripeParity,
    };

    [Fact]
    public void Serialize_Deserialize_RoundTripsAllFields()
    {
        var header = Sample(stripeData: 40, stripeParity: 6);
        var restored = ShardHeader.Deserialize(header.Serialize(), out int len);

        Assert.NotNull(restored);
        Assert.Equal(header.FileId, restored.FileId);
        Assert.Equal(header.Index, restored.Index);
        Assert.Equal(header.Count, restored.Count);
        Assert.Equal(header.PayloadLength, restored.PayloadLength);
        Assert.Equal(header.PayloadCrc32, restored.PayloadCrc32);
        Assert.Equal(header.TotalLength, restored.TotalLength);
        Assert.Equal(header.OriginalLength, restored.OriginalLength);
        Assert.Equal(header.Flags, restored.Flags);
        Assert.Equal(header.Sha256, restored.Sha256);
        Assert.Equal(header.FileName, restored.FileName);
        Assert.Equal(header.StripeData, restored.StripeData);
        Assert.Equal(header.StripeParity, restored.StripeParity);
        Assert.False(restored.IsParity);
        Assert.Equal(header.Serialize().Length, len);
    }

    [Fact]
    public void ParityHeader_RoundTrips_AndAllowsIndexBeyondCount()
    {
        // Parity images carry a global parity ordinal that legitimately exceeds Count.
        var header = Sample(index: 30, count: 12, flags: ShardHeader.FlagParity, stripeData: 12, stripeParity: 4);
        var restored = ShardHeader.Deserialize(header.Serialize(), out _);
        Assert.NotNull(restored);
        Assert.True(restored.IsParity);
        Assert.Equal(30, restored.Index);
        Assert.Equal(4, restored.StripeParity);
    }

    [Fact]
    public void ShardHeader_IsSampleWithDefaultedStripeFields()
    {
        var restored = ShardHeader.Deserialize(Sample().Serialize(), out _);
        Assert.NotNull(restored);
        Assert.Equal(0, restored.StripeData);
        Assert.Equal(0, restored.StripeParity);
        Assert.False(restored.IsParity);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("report.pdf")]
    [InlineData("файл-数据-🎉.bin")]
    [InlineData("")]
    public void Size_MatchesSerializedLength(string name)
    {
        var header = Sample(name);
        Assert.Equal(ShardHeader.Size(name), header.Serialize().Length);
    }

    [Fact]
    public void Deserialize_UnicodeFileName_RoundTrips()
    {
        var restored = ShardHeader.Deserialize(Sample("файл-数据-🎉.bin").Serialize(), out _);
        Assert.Equal("файл-数据-🎉.bin", restored!.FileName);
    }

    [Fact]
    public void Deserialize_ToleratesTrailingPayloadBytes()
    {
        byte[] serialized = Sample().Serialize();
        byte[] withPayload = [.. serialized, .. new byte[500]];
        var restored = ShardHeader.Deserialize(withPayload, out int len);
        Assert.NotNull(restored);
        Assert.Equal(serialized.Length, len);
    }

    [Fact]
    public void Deserialize_AnyCorruptedByte_IsRejected()
    {
        byte[] serialized = Sample().Serialize();
        for (int i = 0; i < serialized.Length; i++)
        {
            serialized[i] ^= 0x55;
            Assert.Null(ShardHeader.Deserialize(serialized, out _));
            serialized[i] ^= 0x55;
        }
        Assert.NotNull(ShardHeader.Deserialize(serialized, out _));
    }

    [Fact]
    public void Deserialize_Truncated_IsRejected()
    {
        byte[] serialized = Sample().Serialize();
        for (int len = 0; len < serialized.Length; len += 5)
            Assert.Null(ShardHeader.Deserialize(serialized[..len], out _));
    }

    [Fact]
    public void Deserialize_WrongMagic_IsRejected()
    {
        byte[] serialized = Sample().Serialize();
        serialized[0] = (byte)'X';
        Assert.Null(ShardHeader.Deserialize(serialized, out _));
    }

    [Fact]
    public void Deserialize_IndexOutOfRange_IsRejected()
    {
        // index >= count is structurally invalid even with a valid CRC.
        var header = Sample(index: 7, count: 7);
        Assert.Null(ShardHeader.Deserialize(header.Serialize(), out _));
    }

    [Fact]
    public void Deserialize_OversizedFileName_IsRejected()
    {
        var header = Sample(new string('x', 5000));
        Assert.Null(ShardHeader.Deserialize(header.Serialize(), out _));
    }
}
