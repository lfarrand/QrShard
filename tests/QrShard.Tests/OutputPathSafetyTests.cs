using System.Security.Cryptography;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The file name in a shard header is attacker-controlled: decode consumes untrusted images, and
/// nothing stops a hand-crafted shard from carrying "../../x" or an absolute path. These pin the
/// invariant that such a name can never steer the write outside the directory the caller chose.
/// </summary>
public class OutputPathSafetyTests
{
    private static DecodedShard CraftShard(string fileName, byte[] content)
    {
        var header = new ShardHeader
        {
            FileId = 0xABCDEF,
            Index = 0,
            Count = 1,
            PayloadLength = content.Length,
            PayloadCrc32 = new Crc().Crc32(content),
            TotalLength = content.Length,
            OriginalLength = content.Length,
            Flags = 0,
            Sha256 = SHA256.HashData(content),
            FileName = fileName,
            StripeData = 0,
            StripeParity = 0,
        };
        return new DecodedShard(header, content, "crafted.png", 0, 0);
    }

    private static void AssembleIn(string workingDirectory, DecodedShard shard)
    {
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = workingDirectory;
            new ShardAssembler().Assemble([shard], null, _ => { });
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void RelativeTraversalInHeaderFileName_CannotEscapeTheWorkingDirectory()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        string outside = Path.Combine(tmp.Path, "outside");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(outside);

        AssembleIn(cwd, CraftShard(Path.Combine("..", "outside", "escaped.bin"), TestData.Random(64)));

        Assert.False(File.Exists(Path.Combine(outside, "escaped.bin")),
            "a '..' in the header file name escaped the working directory");
    }

    [Fact]
    public void AbsolutePathInHeaderFileName_IsConfinedToTheWorkingDirectory()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);

        // Path.Combine returns its second argument verbatim when that argument is rooted, so an
        // unsanitized absolute name would land exactly here instead of under the working directory.
        string absolute = Path.Combine(tmp.Path, "absolute-target.bin");
        AssembleIn(cwd, CraftShard(absolute, TestData.Random(64)));

        Assert.False(File.Exists(absolute),
            "an absolute header file name wrote outside the working directory");
        Assert.NotEmpty(Directory.GetFiles(cwd)); // it still restored, just in the right place
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void DegenerateHeaderFileNames_StillProduceAFileInsideTheWorkingDirectory(string name)
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);

        AssembleIn(cwd, CraftShard(name, TestData.Random(32)));

        // "." and ".." survive Path.GetFileName intact and would otherwise resolve to a directory.
        Assert.NotEmpty(Directory.GetFiles(cwd));
    }

    // Deliberately identical on every OS: '\' is an ordinary filename character on Linux, so
    // relying on Path.GetFileName would let a Windows-style name through there. Shards cross
    // platforms by design, so the same input must reduce the same way everywhere.
    [Theory]
    [InlineData("../../x.bin", "x.bin")]
    [InlineData(@"..\..\x.bin", "x.bin")]
    [InlineData("sub/dir/x.bin", "x.bin")]
    [InlineData(@"C:\Windows\System32\x.bin", "x.bin")]
    [InlineData("/etc/cron.d/x", "x")]
    [InlineData("plain.bin", "plain.bin")]
    [InlineData("..", "restored.bin")]
    [InlineData(".", "restored.bin")]
    [InlineData("", "restored.bin")]
    [InlineData("C:x.bin", "restored.bin")]      // Windows drive-relative
    [InlineData("x.bin:stream", "restored.bin")] // NTFS alternate data stream
    [InlineData("a\0b", "restored.bin")]
    public void SafeFileName_ReducesToABareName(string input, string expected) =>
        Assert.Equal(expected, ShardAssembler.SafeFileName(input));

    [Fact]
    public void SafeFileName_LeavesTheHeaderValueItselfUntouched()
    {
        // The original name is still what gets logged and bound as AES-GCM associated data, so
        // sanitizing the stored value (rather than only the path) would break decryption of
        // shards already in the wild.
        var shard = CraftShard("../../x.bin", TestData.Random(16));
        Assert.Equal("../../x.bin", shard.Header.FileName);
    }
}
