using System.Formats.Tar;
using System.Security.Cryptography;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The archive branch of reassembly derives its destination directory from the same
/// attacker-controlled header file name as the single-file branch. OutputPathSafetyTests only
/// ever crafts shards with Flags = 0, so it does not reach here — which is how this path kept a
/// traversal after the single-file one was fixed.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class ArchiveOutputPathSafetyTests
{
    private static byte[] BuildTar(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(content),
            };
            w.WriteEntry(entry);
        }
        return ms.ToArray();
    }

    private static DecodedShard CraftArchiveShard(string headerFileName, byte[] tar)
    {
        var header = new ShardHeader
        {
            FileId = 0x5150,
            Index = 0,
            Count = 1,
            PayloadLength = tar.Length,
            PayloadCrc32 = new Crc().Crc32(tar),
            TotalLength = tar.Length,
            OriginalLength = tar.Length,
            Flags = ShardHeader.FlagArchive,
            Sha256 = SHA256.HashData(tar),
            FileName = headerFileName,
            StripeData = 0,
            StripeParity = 0,
        };
        return new DecodedShard(header, tar, "crafted.png", 0, 0);
    }

    /// <summary>
    /// "..." is the payload: Path.GetFileNameWithoutExtension("...") returns "..", which combines
    /// to the parent of the working directory. Neither "." nor ".." survives SafeFileName, but
    /// "..." does — and only becomes traversing after the extension is stripped.
    /// </summary>
    [Theory]
    [InlineData("...")]
    [InlineData("....tar")]
    [InlineData("..")]
    [InlineData(".")]
    public void ArchiveHeaderName_CannotExtractOutsideTheWorkingDirectory(string headerName)
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);

        var shard = CraftArchiveShard(headerName, BuildTar("pwned.txt", "owned"u8.ToArray()));

        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd;
            new ShardAssembler().Assemble([shard], null, _ => { });
        }
        catch (ShardDecodeException)
        {
            // Refusing outright is an acceptable outcome; escaping is not.
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        var escaped = Directory.GetFiles(tmp.Path, "pwned.txt", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.True(escaped.Count == 0,
            $"header name '{headerName}' extracted outside the working directory: {string.Join(", ", escaped)}");
    }

    [Fact]
    public void AnOrdinaryArchiveName_StillExtractsNormally()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);

        var shard = CraftArchiveShard("photos.tar", BuildTar("a/b.txt", "hello"u8.ToArray()));

        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd;
            new ShardAssembler().Assemble([shard], null, _ => { });
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.NotEmpty(Directory.GetFiles(cwd, "b.txt", SearchOption.AllDirectories));
    }
}
