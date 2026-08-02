using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Pins SPEC.md §9.3 to the implementation. The spec is normative — someone builds an
/// independent decoder from it — so the AAD layout it documents is asserted here byte-for-byte
/// rather than trusted to stay in step.
/// </summary>
public class SpecAadConformanceTests
{
    [Fact]
    public void Aad_MatchesTheLayoutSpecIn_9_3()
    {
        const long originalLength = 0x0102030405060708;
        byte[] sha = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        const string name = "réport ✓.pdf"; // non-ASCII: the spec says UTF-8, no terminator

        byte[] actual = PayloadCipher.BuildAad(originalLength, sha, name);

        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        Assert.Equal(40 + nameBytes.Length, actual.Length);          // "40 + n bytes total"
        Assert.Equal(originalLength, BinaryPrimitives.ReadInt64LittleEndian(actual));  // LE int64
        Assert.Equal(sha, actual[8..40]);                             // sha256 at offset 8
        Assert.Equal(nameBytes, actual[40..]);                        // UTF-8, no terminator
    }

    [Fact]
    public void EmptyOriginal_UsesZeroLengthAndTheShaOfNoBytes()
    {
        // §9.3: "For an empty original file the encoder encrypts an empty plaintext with
        // originalLength = 0 and the SHA-256 of zero bytes."
        byte[] shaOfNothing = SHA256.HashData([]);
        byte[] aad = PayloadCipher.BuildAad(0, shaOfNothing, "empty.bin");
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(aad));
        Assert.Equal(shaOfNothing, aad[8..40]);
    }

    [Fact]
    public void EveryFlagInTheSpecTable_IsTheValueTheCodeUses()
    {
        // SPEC.md §4.1 must not drift from the constants again.
        Assert.Equal(0x01, ShardHeader.FlagCompressed);
        Assert.Equal(0x02, ShardHeader.FlagParity);
        Assert.Equal(0x04, ShardHeader.FlagBrotli);
        Assert.Equal(0x08, ShardHeader.FlagEncrypted);
        Assert.Equal(0x10, ShardHeader.FlagArchive);
        Assert.Equal(0x20, ShardHeader.FlagFountain);
        Assert.Equal(0x40, ShardHeader.FlagAuthMeta);
        Assert.Equal(0x80, ShardHeader.FlagAuthMetaV2);
        Assert.Equal(0xFF, ShardHeader.KnownFlags);
    }

    [Fact]
    public void AuthMetaWithoutEncryption_IsRejectedAsTheSpecRequires()
    {
        var header = new ShardHeader
        {
            FileId = 1,
            Index = 0,
            Count = 1,
            PayloadLength = 0,
            PayloadCrc32 = 0,
            TotalLength = 0,
            OriginalLength = 0,
            Flags = ShardHeader.FlagAuthMeta,
            Sha256 = SHA256.HashData([]),
            FileName = "invalid.bin",
        };

        Assert.Null(ShardHeader.Deserialize(header.Serialize(), out _));
    }

    [Fact]
    public void AuthMetaV2WithoutItsRequiredEncryptionSuite_IsRejected()
    {
        var header = new ShardHeader
        {
            FileId = 2,
            Index = 0,
            Count = 1,
            PayloadLength = 0,
            PayloadCrc32 = 0,
            TotalLength = 0,
            OriginalLength = 0,
            Flags = 0x80,
            Sha256 = SHA256.HashData([]),
            FileName = "invalid-v2.bin",
        };

        Assert.Null(ShardHeader.Deserialize(header.Serialize(), out _));
    }

    [Fact]
    public void MalformedUtf8Filename_IsRejectedInsteadOfCanonicalizedForAad()
    {
        var header = new ShardHeader
        {
            FileId = 3,
            Index = 0,
            Count = 1,
            PayloadLength = 0,
            PayloadCrc32 = 0,
            TotalLength = 0,
            OriginalLength = 0,
            Flags = ShardHeader.FlagEncrypted | ShardHeader.FlagAuthMeta,
            Sha256 = SHA256.HashData([]),
            FileName = "\uFFFD",
        };
        byte[] bytes = header.Serialize();

        // Filename starts at byte 88. Keep its encoded length at three bytes, but replace the
        // valid EF BF BD encoding with a malformed three-byte sequence and repair the public
        // header CRC, just as an untrusted shard producer can.
        bytes[88] = 0xE2;
        bytes[89] = 0x28;
        bytes[90] = 0xA1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4),
            new Crc().Crc32(bytes.AsSpan(0, bytes.Length - 4)));

        Assert.Null(ShardHeader.Deserialize(bytes, out _));
    }

    [Fact]
    public void SpecDocuments_EveryFlagBitTheCodeKnows()
    {
        // Reads SPEC.md itself: each bit in KnownFlags must appear in the §4.1 table. This is the
        // check that would have caught 0x40 being absent while the encoder set it on every
        // encrypted shard.
        string spec = File.ReadAllText(Path.Combine(SolutionRoot(), "SPEC.md"));
        for (int bit = 0x01; bit <= 0x80; bit <<= 1)
        {
            if ((ShardHeader.KnownFlags & bit) == 0)
                continue;
            Assert.True(spec.Contains($"| 0x{bit:X2} |", StringComparison.OrdinalIgnoreCase),
                $"SPEC.md §4.1 does not document flag 0x{bit:X2}, which this build sets or accepts");
        }
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPEC.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
