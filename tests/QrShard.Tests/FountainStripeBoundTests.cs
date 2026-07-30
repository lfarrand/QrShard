using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The 255-shard ceiling belongs to the Cauchy layer, whose parity rows and data columns index one
/// shared GF(2^8) domain. Fountain coefficients are random rather than domain-limited, so the
/// ceiling does not apply to it — StripePlanner.PlanFountain says exactly that, and SPEC section 8
/// imposes no such limit while section 7 does.
///
/// A header validation added for the Cauchy path applied that sum check to every header with
/// parity, fountain included. Encode still succeeded, so a high `-F` produced a folder of images
/// that no decoder could read, blaming the capture ("Shard header is corrupt. Recapture this
/// image.") rather than the flag.
/// </summary>
public class FountainStripeBoundTests
{
    private static ShardHeader Header(byte flags, int stripeData, int stripeParity) => new()
    {
        FileId = 0x0123456789abcdef,
        Index = 0,
        Count = 64,
        PayloadLength = 8,
        PayloadCrc32 = 0,
        TotalLength = 8,
        OriginalLength = 8,
        Flags = flags,
        Sha256 = new byte[32],
        FileName = "x.bin",
        StripeData = stripeData,
        StripeParity = stripeParity,
    };

    [Fact]
    public void FountainHeader_MayExceedTheCauchyShardCeiling()
    {
        // -F 400 over the 64-image fountain stripe orders 256 coded frames: a sum of 320, far past
        // the Cauchy ceiling and entirely legal for a fountain set.
        var header = Header(ShardHeader.FlagFountain | ShardHeader.FlagParity, stripeData: 64, stripeParity: 256);
        Assert.NotNull(ShardHeader.Deserialize(header.Serialize(), out _));
    }

    [Fact]
    public void CauchyHeader_StillMayNot()
    {
        // The bound is load-bearing here: past 255 the byte casts in CrossShardFec.ParityMatrix
        // wrap until a parity row collides with a data column, x ^ y is zero, and Inv(0) throws.
        var header = Header(ShardHeader.FlagParity, stripeData: 200, stripeParity: 100);
        Assert.Null(ShardHeader.Deserialize(header.Serialize(), out _));

        var ok = Header(ShardHeader.FlagParity, stripeData: 200, stripeParity: 55); // 255 exactly
        Assert.NotNull(ShardHeader.Deserialize(ok.Serialize(), out _));
    }

    [Fact]
    public void HighFountainPercent_RoundTrips()
    {
        // End to end, because the header unit test alone would not have caught the encoder writing
        // a value its own decoder rejects. Enough images to fill the 64-wide fountain stripe, at a
        // percentage that pushes the sum past 255.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(900_000);
        string input = tmp.WriteFile("input.bin", content);

        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 700, Height = 700, CellPx = 3, BitsPerCell = 2, FountainPercent = 400 });

        Assert.True(result.ImageCount >= FountainFec.MaxStripeData,
            $"need a full {FountainFec.MaxStripeData}-image stripe to exceed the ceiling; got {result.ImageCount}");

        string restored = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }
}
