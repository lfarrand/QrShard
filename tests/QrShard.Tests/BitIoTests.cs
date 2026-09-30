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
    [InlineData(9)]
    [InlineData(10)]
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void BitStream_CellWindow_MatchesBitWriter_AtEveryAlignment(int bits)
    {
        var stream = new BitStream();
        int mask = (1 << bits) - 1;
        int[] values = [mask, 0x2A5 & mask];
        foreach (int value in values)
        {
            for (int align = 0; align < 8; align++)
            {
                int span = ((align + bits - 1) >> 3) + 1;
                var writer = new BitWriter();
                if (align > 0)
                    writer.Write(0, align);
                writer.Write((uint)value, bits);
                byte[] expected = writer.ToArray();
                Assert.Equal(span, expected.Length);

                var buffer = new byte[span];
                stream.WriteCell(buffer, align, bits, value);
                Assert.Equal(expected, buffer);
                Assert.Equal(value, stream.ReadCell(buffer, align, bits));

                var masked = new byte[span];
                stream.WriteCell(masked, align, bits, value | ~mask);
                Assert.Equal(expected, masked);

                if (span < 2)
                    continue;
                var shortWritten = new byte[span - 1];
                stream.WriteCell(shortWritten, align, bits, value);
                Assert.Equal(expected[..^1], shortWritten);
                int dropped = bits - ((span - 1) * 8 - align);
                Assert.Equal((value >> dropped) << dropped, stream.ReadCell(shortWritten, align, bits));
            }
        }

        var untouched = new byte[2];
        stream.WriteCell(untouched, 16, bits, mask);
        Assert.Equal(new byte[2], untouched);
        Assert.Equal(0, stream.ReadCell(untouched, 16, bits));
    }

    [Fact]
    public void BitStream_TenBitCell_SpansThreeBytes_AndStopsAtBufferEnd()
    {
        var stream = new BitStream();
        // Offset 7 is the only alignment where 10 bits cross three bytes.
        var full = new byte[] { 0xFE, 0x00, 0x7F };
        stream.WriteCell(full, 7, 10, 0x3FF);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, full);
        Assert.Equal(0x3FF, stream.ReadCell(full, 7, 10));

        var patterned = new byte[3];
        stream.WriteCell(patterned, 7, 10, 0x2A5); // 0b10_1010_0101
        Assert.Equal(new byte[] { 0x01, 0x52, 0x80 }, patterned);
        Assert.Equal(0x2A5, stream.ReadCell(patterned, 7, 10));

        var two = new byte[2];
        stream.WriteCell(two, 7, 10, 0x3FF);
        Assert.Equal(new byte[] { 0x01, 0xFF }, two);
        Assert.Equal(0x3FE, stream.ReadCell(two, 7, 10));

        var one = new byte[1];
        stream.WriteCell(one, 7, 10, 0x3FF);
        Assert.Equal(0x01, one[0]);
        Assert.Equal(0x200, stream.ReadCell(one, 7, 10));

        var none = new byte[1];
        stream.WriteCell(none, 8, 10, 0x3FF);
        Assert.Equal(0, none[0]);
        Assert.Equal(0, stream.ReadCell(none, 8, 10));
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
