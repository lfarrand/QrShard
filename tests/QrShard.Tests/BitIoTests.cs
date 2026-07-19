using QrShard;

namespace QrShard.Tests;

public class BitIoTests
{
    [Fact]
    public void BitWriter_WritesMsbFirst()
    {
        var w = new BitWriter();
        w.Write(0b1, 1);
        w.Write(0b0, 1);
        w.Write(0b11, 2);
        w.Write(0b0000, 4);
        Assert.Equal(new byte[] { 0b1011_0000 }, w.ToArray());
    }

    [Fact]
    public void BitWriter_BitReader_RoundTripMixedWidths()
    {
        var values = new (uint value, int bits)[]
        {
            (0xC5, 8), (1, 4), (8, 4), (65535, 16), (0, 16), (300, 9), (1, 1), (0x12345678, 32),
        };
        var w = new BitWriter();
        foreach (var (value, bits) in values)
            w.Write(value, bits);

        var r = new BitReader(w.ToArray());
        foreach (var (value, bits) in values)
            Assert.Equal(value, r.Read(bits));
    }

    [Fact]
    public void BitReader_PastEnd_ReadsZero()
    {
        var r = new BitReader([0xFF]);
        Assert.Equal(0xFFu, r.Read(8));
        Assert.Equal(0u, r.Read(8));
        Assert.Equal(0u, r.Read(32));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void BitStream_ReadWriteCell_RoundTripsAllValues(int bits)
    {
        int cells = 200;
        var expected = new int[cells];
        var rng = new Random(bits);
        var buffer = new byte[(cells * bits + 7) / 8];
        for (int i = 0; i < cells; i++)
        {
            expected[i] = rng.Next(1 << bits);
            new BitStream().WriteCell(buffer, (long)i * bits, bits, expected[i]);
        }
        for (int i = 0; i < cells; i++)
            Assert.Equal(expected[i], new BitStream().ReadCell(buffer, (long)i * bits, bits));
    }

    [Fact]
    public void BitStream_ReadCell_PastBuffer_ReturnsZero()
    {
        var buffer = new byte[] { 0xFF };
        Assert.Equal(0, new BitStream().ReadCell(buffer, 8, 8));
        Assert.Equal(0b1100, new BitStream().ReadCell(buffer, 6, 4)); // straddles the end
    }

    [Fact]
    public void BitStream_WriteCell_PastBuffer_IsDropped()
    {
        var buffer = new byte[1];
        new BitStream().WriteCell(buffer, 8, 8, 0xFF);  // fully out of range
        new BitStream().WriteCell(buffer, 6, 4, 0b1111); // straddles: only first 2 bits land
        Assert.Equal(0b0000_0011, buffer[0]);
    }

    [Fact]
    public void BitStream_WriteCell_MasksOversizedValues()
    {
        // A value wider than `bits` must not bleed into neighboring cells.
        var buffer = new byte[2];
        new BitStream().WriteCell(buffer, 4, 4, 0xFFF); // only the low 4 bits may land
        Assert.Equal(0b0000_1111, buffer[0]);
        Assert.Equal(0, buffer[1]);
    }

    [Fact]
    public void BitStream_MatchesBitWriterLayout()
    {
        // The encoder writes cells with BitStream against a stream produced conceptually by
        // BitWriter; both must agree on MSB-first ordering.
        var w = new BitWriter();
        w.Write(0xABCD, 16);
        byte[] viaWriter = w.ToArray();

        var viaStream = new byte[2];
        new BitStream().WriteCell(viaStream, 0, 8, 0xAB);
        new BitStream().WriteCell(viaStream, 8, 8, 0xCD);
        Assert.Equal(viaWriter, viaStream);
    }
}
