using System.Formats.Tar;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
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
        => BuildTar((entryName, content));

    private static byte[] BuildTar(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var w = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content),
                };
                w.WriteEntry(entry);
            }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitDestination_ExtractsWithOrWithoutATrailingSeparator(bool trailingSeparator)
    {
        // Path.GetFullPath PRESERVES a trailing separator, so `-o out/` produced a destRoot of
        // "…/out/" and the containment prefix became "…/out//" — a doubled separator that no
        // normalised target can start with, so EVERY entry was rejected as escaping. `-o out/`
        // is an ordinary thing to type (and shell tab-completion supplies it), and the failure
        // looked like a corrupt archive rather than a path-formatting quirk. Filesystem roots
        // carry the separator inherently and failed identically.
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "out");
        Directory.CreateDirectory(dest);
        string destArg = trailingSeparator ? dest + Path.DirectorySeparatorChar : dest;

        var shard = CraftArchiveShard("photos.tar", BuildTar("a/b.txt", "hello"u8.ToArray()));
        new ShardAssembler().Assemble([shard], destArg, _ => { });

        string extracted = Path.Combine(dest, "a", "b.txt");
        Assert.True(File.Exists(extracted), $"nothing extracted for destination '{destArg}'");
        Assert.Equal("hello", File.ReadAllText(extracted));
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

    [Fact]
    public void AutomaticArchiveName_SkipsFilesAtTheBaseAndFirstFallback()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);
        File.WriteAllText(Path.Combine(cwd, "bundle"), "keep base");
        File.WriteAllText(Path.Combine(cwd, "bundle.restored"), "keep fallback");
        var shard = CraftArchiveShard("bundle.tar", BuildTar("a.txt", "payload"u8.ToArray()));

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

        Assert.Equal("keep base", File.ReadAllText(Path.Combine(cwd, "bundle")));
        Assert.Equal("keep fallback", File.ReadAllText(Path.Combine(cwd, "bundle.restored")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(cwd, "bundle.restored-2", "a.txt")));
    }

    [Fact]
    public void ExplicitNonEmptyDestination_IsNeverMergedOrOverwritten()
    {
        using var tmp = new TempDir();
        string dest = tmp.Sub("existing");
        string original = tmp.WriteFile(Path.Combine("existing", "a.txt"), "ORIGINAL"u8.ToArray());
        var shard = CraftArchiveShard("bundle.tar", BuildTar("a.txt", "replacement"u8.ToArray()));

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));

        Assert.Equal("ORIGINAL", File.ReadAllText(original));
        Assert.Single(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void InvalidLateEntry_PublishesNoPartialArchive()
    {
        // The first entry is valid and used to be written before the second entry's traversal was
        // discovered. Extraction now happens off to the side, so neither entry becomes visible.
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "restore");
        string escaped = Path.Combine(tmp.Path, "escape.txt");
        byte[] tar = BuildTar(
            ("safe.txt", "partial"u8.ToArray()),
            ("../escape.txt", "escape"u8.ToArray()));
        var shard = CraftArchiveShard("bundle.tar", tar);

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));

        Assert.False(Directory.Exists(dest));
        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public void InvalidArchiveDoesNotPreventALaterIndependentFileFromRestoring()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);
        byte[] invalidTar = BuildTar("../escape.txt", "bad archive"u8.ToArray());
        var bad = CraftArchiveShard("bad.tar", invalidTar);
        byte[] goodBytes = "keep this file"u8.ToArray();
        var goodHeader = new ShardHeader
        {
            FileId = 0x5151, Index = 0, Count = 1, PayloadLength = goodBytes.Length,
            PayloadCrc32 = new Crc().Crc32(goodBytes), TotalLength = goodBytes.Length,
            OriginalLength = goodBytes.Length, Flags = 0, Sha256 = SHA256.HashData(goodBytes),
            FileName = "good.bin", StripeData = 0, StripeParity = 0,
        };
        var good = new DecodedShard(goodHeader, goodBytes, "good.png", 0, 0);

        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd;
            Assert.Throws<ShardDecodeException>(() =>
                new ShardAssembler().Assemble([bad, good], null, _ => { }));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(goodBytes, File.ReadAllBytes(Path.Combine(cwd, "good.bin")));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("safe/CON.txt")]
    [InlineData("trailing-dot.")]
    [InlineData("alternate:stream")]
    [InlineData("back\\slash.txt")]
    [InlineData("COM¹.txt")]
    [InlineData("LPT³.log")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$.txt")]
    public void NonPortableOrDeviceEntry_IsRejectedWithoutPublishing(string entryName)
    {
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "restore");
        var shard = CraftArchiveShard("bundle.tar", BuildTar(entryName, "data"u8.ToArray()));

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));

        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void HostileArchiveEntryName_IsSanitizedInErrors()
    {
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "restore");
        var shard = CraftArchiveShard("bundle.tar", BuildTar("evil\u001b[31m.txt", [1]));

        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));

        Assert.DoesNotContain('\u001b', ex.Message);
        Assert.Contains("evil?[31m.txt", ex.Message);
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void CaseAliasedEntries_AreRejectedRatherThanOverwritten()
    {
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "restore");
        var shard = CraftArchiveShard("bundle.tar", BuildTar(
            ("A.txt", "first"u8.ToArray()),
            ("a.txt", "second"u8.ToArray())));

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void LinkEntries_AreRejectedRatherThanSilentlyDropped()
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "target" };
            writer.WriteEntry(link);
        }
        using var tmp = new TempDir();
        string dest = Path.Combine(tmp.Path, "restore");
        var shard = CraftArchiveShard("bundle.tar", ms.ToArray());

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([shard], dest, _ => { }));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsArchiveStagingDirectoryHasAProtectedOwnerOnlyAclAtCreation()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string staging = Path.Combine(tmp.Path, "private-stage");

        ShardAssembler.CreatePrivateWindowsDirectory(staging);

        DirectorySecurity acl = new DirectoryInfo(staging).GetAccessControl(AccessControlSections.Access);
        Assert.True(acl.AreAccessRulesProtected);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(current, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        });
        Assert.Contains(rules, rule =>
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
            (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) ==
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
    }

    [Fact]
    public void ExistingEmptyArchiveDestination_PreservesUnixDirectoryPolicyAfterPublish()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string dest = tmp.Sub("restore");
        const UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                      UnixFileMode.GroupExecute | UnixFileMode.SetGroup |
                                      UnixFileMode.StickyBit;
        File.SetUnixFileMode(dest, expected);

        new ShardAssembler().Assemble(
            [CraftArchiveShard("bundle.tar", BuildTar("proof.txt", "verified"u8.ToArray()))],
            dest, _ => { });

        Assert.Equal(expected, File.GetUnixFileMode(dest));
        Assert.Equal("verified", File.ReadAllText(Path.Combine(dest, "proof.txt")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExistingEmptyArchiveDestination_PreservesExplicitWindowsDaclAfterPublish()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string dest = tmp.Sub("restore");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var expected = new DirectorySecurity();
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        expected.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        expected.AddAccessRule(new FileSystemAccessRule(world, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dest).SetAccessControl(expected);

        new ShardAssembler().Assemble(
            [CraftArchiveShard("bundle.tar", BuildTar("proof.txt", "verified"u8.ToArray()))],
            dest, _ => { });

        DirectorySecurity actual = new DirectoryInfo(dest).GetAccessControl(AccessControlSections.Access);
        Assert.True(actual.AreAccessRulesProtected);
        var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.Contains(rules, rule => !rule.IsInherited && world.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
        Assert.Equal("verified", File.ReadAllText(Path.Combine(dest, "proof.txt")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ArchivePublish_CompletesBeforeRestoringDaclThatDeniesWriteAttributes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string dest = tmp.Sub("restricted-restore");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var restricted = new DirectorySecurity();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const FileSystemRights finalRights = FileSystemRights.ReadAndExecute | FileSystemRights.Delete;
        restricted.AddAccessRule(new FileSystemAccessRule(current, finalRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
        new DirectoryInfo(dest).SetAccessControl(restricted);
        string expectedDacl = new DirectoryInfo(dest).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        new ShardAssembler().Assemble(
            [CraftArchiveShard("bundle.tar", BuildTar("proof.txt", "verified"u8.ToArray()))],
            dest, _ => { });

        Assert.True((File.GetAttributes(dest) & FileAttributes.Hidden) != 0);
        DirectorySecurity actual = new DirectoryInfo(dest).GetAccessControl(AccessControlSections.Access);
        Assert.Equal(expectedDacl, actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        var rule = Assert.Single(actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>());
        Assert.Equal(current, rule.IdentityReference);
        Assert.Equal((FileSystemRights)0, rule.FileSystemRights & FileSystemRights.WriteAttributes);
        Assert.Equal("verified", File.ReadAllText(Path.Combine(dest, "proof.txt")));
    }
}
