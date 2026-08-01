using QrShard;
using System.Security.Cryptography;

namespace QrShard.Tests;

/// <summary>
/// Adversarial shard-header validation: a header is CRC-valid but its stripe geometry is
/// crafted. These fields drive divisor and array-size math in the reassembler and the
/// video-decode completeness check, so a bad combination must be rejected at deserialization —
/// never reach the math as a DivideByZeroException or OverflowException.
/// </summary>
public class HeaderHardeningTests
{
    private static ShardHeader FamilyHeader(string variant = "base")
    {
        byte[] sha = new byte[32];
        if (variant == "sha") sha[0] = 1;
        return new ShardHeader
        {
            FileId = 0xCAFE,
            Index = variant == "index" ? 1 : 0,
            Count = variant == "count" ? 2 : 1,
            PayloadLength = 4,
            PayloadCrc32 = 0,
            TotalLength = variant == "total" ? 5 : 4,
            OriginalLength = variant == "original" ? 5 : 4,
            Flags = variant == "flags" ? ShardHeader.FlagArchive : (byte)0,
            Sha256 = sha,
            FileName = variant == "name" ? "other.bin" : "x.bin",
            StripeData = variant == "stripe-data" ? 1 : 0,
            StripeParity = variant == "stripe-parity" ? 1 : 0,
        };
    }

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

    [Theory]
    // The geometry fields had lower bounds but no upper bounds, so their products could overflow
    // int or allocate absurd arrays in the reassembler. Each of these is CRC-valid yet must be
    // rejected at deserialize.
    [InlineData(100_000, 1, 30_000)]      // stripes*StripeParity = 3e9 → overflowed int (the reported bug)
    [InlineData(5_000_000, 1, 21)]        // 5e6 * 21 = 1.05e8 > 100M ordinal ceiling
    [InlineData(10_000_000, 8, 2)]        // Count above MaxImages
    [InlineData(1_000, 999, 2)]           // stripeData above a stripe's capacity (255)
    public void CraftedOversizeGeometry_IsRejectedAtDeserialize(int count, int stripeData, int stripeParity)
    {
        byte[] bytes = BuildHeaderBytes(count, stripeData, stripeParity, ShardHeader.FlagParity);
        Assert.Null(ShardHeader.Deserialize(bytes, out _));
    }

    [Fact]
    public void PlausibleLargeCauchyGeometry_StillAccepted()
    {
        // A big-but-legal encode: 2M data images, full 254-wide Cauchy stripes with 1 parity each
        // (stripes ≈ 7875, ordinal space ≈ 7875 ≪ ceiling). Must not be caught by the new bounds.
        Assert.NotNull(ShardHeader.Deserialize(BuildHeaderBytes(2_000_000, 254, 1, ShardHeader.FlagParity), out _));
    }

    [Fact]
    public void FormerlyCrashingHeader_ReachingIsSetComplete_DoesNotCrash()
    {
        // Directly constructed (bypassing Deserialize's guards) — the reassembler's own defense
        // must still make this total, never an OverflowException.
        var header = new ShardHeader
        {
            FileId = 1, Index = 0, Count = 100_000, PayloadLength = 4,
            PayloadCrc32 = new Crc().Crc32([1, 2, 3, 4]), TotalLength = 4, OriginalLength = 4,
            Flags = ShardHeader.FlagParity, Sha256 = new byte[32], FileName = "x",
            StripeData = 1, StripeParity = 30_000,
        };
        var shard = new DecodedShard(header, [1, 2, 3, 4], "crafted", 0, 0);
        var ex = Record.Exception(() => new ParityReassembler().IsSetComplete([shard]));
        Assert.True(ex is null or ShardDecodeException, $"unexpected {ex?.GetType().Name}: {ex?.Message}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsSetComplete_RejectsDirectlyConstructedNegativeOrdinalsWithoutIndexing(bool parity)
    {
        var header = new ShardHeader
        {
            FileId = 7, Index = -1, Count = 1, PayloadLength = 1,
            PayloadCrc32 = 0, TotalLength = 1, OriginalLength = 1,
            Flags = parity ? ShardHeader.FlagParity : (byte)0,
            Sha256 = new byte[32], FileName = "negative.bin",
            StripeData = 1, StripeParity = 1,
        };
        var shard = new DecodedShard(header, [1], "direct", 0, 0);

        var ex = Record.Exception(() => new ParityReassembler().IsSetComplete([shard]));

        Assert.Null(ex);
        Assert.False(new ParityReassembler().IsSetComplete([shard]));
    }

    [Fact]
    public void IsSetComplete_DoesNotStopEarlyOnInconsistentRecoveryCapacity()
    {
        byte[] sha = new byte[32];
        var dataHeader = new ShardHeader
        {
            FileId = 71, Index = 0, Count = 2, PayloadLength = 10,
            PayloadCrc32 = 0, TotalLength = 20, OriginalLength = 20, Flags = 0,
            Sha256 = sha, FileName = "capacity.bin",
            StripeData = 2, StripeParity = 1,
        };
        var parityHeader = new ShardHeader
        {
            FileId = 71, Index = 0, Count = 2, PayloadLength = 11,
            PayloadCrc32 = 0, TotalLength = 20, OriginalLength = 20, Flags = ShardHeader.FlagParity,
            Sha256 = sha, FileName = "capacity.bin", StripeData = 2, StripeParity = 1,
        };
        var shards = new[]
        {
            new DecodedShard(dataHeader, new byte[10], "data", 0, 0),
            new DecodedShard(parityHeader, new byte[11], "parity", 0, 0),
        };

        Assert.False(new ParityReassembler().IsSetComplete(shards));
    }

    [Fact]
    public void IsSetComplete_DoesNotAllocateFromHugeCountOnClearlyIncompleteSet()
    {
        var header = new ShardHeader
        {
            FileId = 72, Index = 0, Count = 5_000_000, PayloadLength = 1, PayloadCrc32 = 0,
            TotalLength = 5_000_000, OriginalLength = 5_000_000, Flags = 0,
            Sha256 = new byte[32], FileName = "sparse.bin", StripeData = 254, StripeParity = 1,
        };
        var shard = new DecodedShard(header, [1], "one", 0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(new ParityReassembler().IsSetComplete([shard]));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"sparse completeness check allocated {allocated:N0} bytes");
    }

    [Theory]
    [InlineData("count")]
    [InlineData("total")]
    [InlineData("original")]
    [InlineData("flags")]
    [InlineData("sha")]
    [InlineData("name")]
    [InlineData("stripe-data")]
    [InlineData("stripe-parity")]
    public void RepeatedFamilyMetadataMustMatch(string variant)
    {
        var first = FamilyHeader();
        var other = FamilyHeader(variant);
        var shards = new[]
        {
            new DecodedShard(first, [1, 2, 3, 4], "a", 0, 0),
            new DecodedShard(other, [1, 2, 3, 4], "b", 0, 0),
        };

        Assert.False(first.HasSameFamilyAs(other));
        Assert.False(new ParityReassembler().IsSetComplete(shards));
        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([.. shards], null, _ => { }));
        Assert.Contains("Inconsistent shard set", ex.Message);
    }

    [Fact]
    public void IndexAndParityMarkerMayDifferWithinOneFamily()
    {
        var data = FamilyHeader();
        var parity = new ShardHeader
        {
            FileId = data.FileId, Index = 7, Count = data.Count, PayloadLength = 8,
            PayloadCrc32 = 123, TotalLength = data.TotalLength, OriginalLength = data.OriginalLength,
            Flags = (byte)(data.Flags | ShardHeader.FlagParity), Sha256 = [.. data.Sha256],
            FileName = data.FileName, StripeData = data.StripeData, StripeParity = data.StripeParity,
        };
        Assert.True(data.HasSameFamilyAs(parity));
    }

    private static ShardHeader RecoveryHeader(int index, byte flags, byte[] content, byte[] whole,
        int count = 2, int stripeParity = 1) => new()
    {
        FileId = 0xBADC0DE,
        Index = index,
        Count = count,
        PayloadLength = content.Length,
        PayloadCrc32 = new Crc().Crc32(content),
        TotalLength = whole.Length,
        OriginalLength = whole.Length,
        Flags = flags,
        Sha256 = SHA256.HashData(whole),
        FileName = "bounded.bin",
        StripeData = 2,
        StripeParity = stripeParity,
    };

    [Fact]
    public void OversizedParityCannotSetChunkCapacityBeforeRecoveryAllocations()
    {
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] firstChunk = whole[..4];
        byte[] craftedParity = new byte[100_000];
        var data = new DecodedShard(RecoveryHeader(0, 0, firstChunk, whole), firstChunk, "data", 0, 0);
        var parity = new DecodedShard(
            RecoveryHeader(0, ShardHeader.FlagParity, craftedParity, whole), craftedParity, "parity", 0, 0);

        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ParityReassembler().ReassembleWithParity([data, parity], data.Header, _ => { }, out _));

        Assert.Contains("capacities are inconsistent", ex.Message);
    }

    [Fact]
    public void CompleteDataIgnoresMalformedOptionalParity()
    {
        using var tmp = new TempDir();
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] a = whole[..4], b = whole[4..];
        byte[] craftedParity = new byte[100_000];
        var data0 = new DecodedShard(RecoveryHeader(0, 0, a, whole), a, "data0", 0, 0);
        var data1 = new DecodedShard(RecoveryHeader(1, 0, b, whole), b, "data1", 0, 0);
        var parity = new DecodedShard(
            RecoveryHeader(0, ShardHeader.FlagParity, craftedParity, whole), craftedParity, "parity", 0, 0);
        string output = tmp.File("out.bin");

        new ShardAssembler().Assemble([parity, data0, data1], output, _ => { });

        Assert.Equal(whole, File.ReadAllBytes(output));
    }

    [Fact]
    public void FountainFramesBeyondInitiallyEmittedOrdinalRemainUsable()
    {
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] a = whole[..4], b = whole[4..];
        const int extraOrdinal = 999;
        byte[] coded = new FountainFec().EncodeFrame(new ArraySegment<byte[]>([a, b]),
            0xBADC0DE, stripe: 0, seq: extraOrdinal, shardLen: 4);
        byte fountain = ShardHeader.FlagFountain;
        var data0 = new DecodedShard(RecoveryHeader(0, fountain, a, whole), a, "data0", 0, 0);
        var parity = new DecodedShard(
            RecoveryHeader(extraOrdinal, (byte)(fountain | ShardHeader.FlagParity), coded, whole),
            coded, "coded", 0, 0);

        byte[][] restored = new ParityReassembler().ReassembleWithParity(
            [data0, parity], data0.Header, _ => { }, out int capacity);

        Assert.Equal(4, capacity);
        Assert.Equal(a, restored[0]);
        Assert.Equal(b, restored[1]);
    }
}
