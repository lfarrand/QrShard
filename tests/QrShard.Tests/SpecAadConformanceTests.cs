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
        Assert.Equal(0x7F, ShardHeader.KnownFlags);   // "any bit outside 0x7F" must be refused
    }

    [Fact]
    public void SpecDocuments_EveryFlagBitTheCodeKnows()
    {
        // Reads SPEC.md itself: each bit in KnownFlags must appear in the §4.1 table. This is the
        // check that would have caught 0x40 being absent while the encoder set it on every
        // encrypted shard.
        string spec = File.ReadAllText(Path.Combine(SolutionRoot(), "SPEC.md"));
        for (int bit = 0x01; bit <= 0x40; bit <<= 1)
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
