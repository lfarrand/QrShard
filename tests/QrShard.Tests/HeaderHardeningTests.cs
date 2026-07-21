using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Adversarial shard-header validation: a header is CRC-valid but its stripe geometry is
/// crafted. These fields drive divisor and array-size math in the reassembler and the
/// video-decode completeness check, so a bad combination must be rejected at deserialization —
/// never reach the math as a DivideByZeroException or OverflowException.
/// </summary>
public class HeaderHardeningTests
{
    private static byte[] BuildHeaderBytes(int count, int stripeData, int stripeParity, byte flags = 0)
    {
        var header = new ShardHeader
        {
            FileId = 0xABCD,
            Index = 0,
            Count = count,
            PayloadLength = 4,
            PayloadCrc32 = new Crc().Crc32([1, 2, 3, 4]),
            TotalLength = 4,
            OriginalLength = 4,
            Flags = flags,
            Sha256 = new byte[32],
            FileName = "x.bin",
            StripeData = stripeData,
            StripeParity = stripeParity,
        };
        return header.Serialize(); // CRC is computed over the crafted fields — a valid header
    }

    [Theory]
    [InlineData(0, 2)]   // parity present, zero stripe data → division by zero
    [InlineData(-1, 2)]  // negative stripe data
    [InlineData(0, 0)]   // both zero with... handled below
    public void CraftedStripeGeometry_IsRejectedAtDeserialize(int stripeData, int stripeParity)
    {
        byte[] bytes = BuildHeaderBytes(count: 4, stripeData, stripeParity);
        var header = ShardHeader.Deserialize(bytes, out _);
        // stripeParity>0 with stripeData<1 is invalid; stripeData=0/parity=0 is the valid
        // "no cross-shard code" case and stays accepted.
        if (stripeParity > 0)
            Assert.Null(header);
        else
            Assert.NotNull(header);
    }

    [Fact]
    public void IsSetComplete_OnCraftedZeroStripeData_DoesNotCrash()
    {
        // Even if such a header somehow reached a shard (it cannot post-fix), the completeness
        // check must be total. Construct a DecodedShard directly and confirm no throw.
        var header = new ShardHeader
        {
            FileId = 1,
            Index = 0,
            Count = 4,
            PayloadLength = 0,
            PayloadCrc32 = 0,
            TotalLength = 0,
            OriginalLength = 0,
            Flags = ShardHeader.FlagParity,
            Sha256 = new byte[32],
            FileName = "x",
            StripeData = 0,
            StripeParity = 2,
        };
        var shard = new DecodedShard(header, [], "crafted", 0, 0);
        var ex = Record.Exception(() => new ParityReassembler().IsSetComplete([shard]));
        Assert.True(ex is null or ShardDecodeException, $"unexpected {ex?.GetType().Name}");
    }

    [Fact]
    public void ValidStripeGeometry_StillAccepted()
    {
        Assert.NotNull(ShardHeader.Deserialize(BuildHeaderBytes(10, 8, 2), out _));
        Assert.NotNull(ShardHeader.Deserialize(BuildHeaderBytes(10, 0, 0), out _)); // no cross-shard code
    }
}
