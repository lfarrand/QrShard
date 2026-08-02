using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The file name in a shard header is attacker-controlled: decode consumes untrusted images, and
/// nothing stops a hand-crafted shard from carrying "../../x" or an absolute path. These pin the
/// invariant that such a name can never steer the write outside the directory the caller chose.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class OutputPathSafetyTests
{
    private static DecodedShard CraftShard(string fileName, byte[] content, ulong fileId = 0xABCDEF,
        byte[]? expectedSha = null)
    {
        var header = new ShardHeader
        {
            FileId = fileId,
            Index = 0,
            Count = 1,
            PayloadLength = content.Length,
            PayloadCrc32 = new Crc().Crc32(content),
            TotalLength = content.Length,
            OriginalLength = content.Length,
            Flags = 0,
            Sha256 = expectedSha ?? SHA256.HashData(content),
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
    // Win32 DOS devices. "<dir>\NUL" opens with FileMode.Create, discards every byte, creates no
    // file, and reports no error — and File.Exists on it is false, so the collision check never
    // diverts. The SHA-256 is computed over the source stream rather than read back, so it still
    // matches: the decode announces "SHA-256 verified" over a file that does not exist.
    [InlineData("NUL", "restored.bin")]
    [InlineData("nul", "restored.bin")]           // resolution is case-insensitive
    [InlineData("NUL.txt", "restored.bin")]       // an extension does not stop it resolving
    [InlineData("con.tar.gz", "restored.bin")]    // nor does more than one
    [InlineData("COM1", "restored.bin")]
    [InlineData("LPT9.bin", "restored.bin")]
    [InlineData("COM¹.txt", "restored.bin")]
    [InlineData("LPT³.log", "restored.bin")]
    [InlineData("CONIN$", "restored.bin")]
    [InlineData("CONOUT$.txt", "restored.bin")]
    [InlineData("AUX", "restored.bin")]
    // Windows strips trailing dots and spaces, so these name the same file as their bare stem —
    // two distinct headers colliding on one path, invisibly to the collision check.
    [InlineData("evil.", "restored.bin")]
    [InlineData("evil ", "restored.bin")]
    // Adjacent non-devices must still pass, so this rejects the device set and not the prefix.
    [InlineData("NULL.bin", "NULL.bin")]
    [InlineData("console.log", "console.log")]
    [InlineData("COM10.bin", "COM10.bin")]
    public void SafeFileName_ReducesToABareName(string input, string expected) =>
        Assert.Equal(expected, ShardAssembler.SafeFileName(input));

    [Fact]
    public void CollisionFallback_KeepsCountingInsteadOfOverwritingItsOwnOutput()
    {
        // The fallback protected the ORIGINAL file and then clobbered whatever already held the
        // fallback name. Assemble resolves one group at a time, so three groups sharing a header
        // FileName sent groups 2 and 3 to the same path and FileMode.Create truncated group 2's
        // output — the tool losing a file it had just successfully restored.
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(cwd);

        var payloads = new[] { TestData.Random(64), TestData.Random(96), TestData.Random(128) };
        string previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd;
            // Distinct FileIds so these are three separate groups, one shared FileName so they
            // all resolve to the same preferred output path.
            var shards = payloads.Select((p, i) => CraftShard("same.bin", p, fileId: 0x1000 + (ulong)i)).ToList();
            new ShardAssembler().Assemble(shards, null, _ => { });
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        var written = Directory.GetFiles(cwd).OrderBy(f => f).ToList();
        Assert.Equal(3, written.Count);
        // Every payload survived: no output was truncated by a later group.
        var onDisk = written.Select(File.ReadAllBytes).ToList();
        foreach (var expected in payloads)
            Assert.Contains(onDisk, actual => actual.SequenceEqual(expected));
    }

    [Fact]
    public void ExplicitOutput_IsUntouchedWhenVerificationFails()
    {
        // -o authorizes replacing a destination only with a fully verified restore. It must not
        // authorize truncating the existing file before decompression/length/SHA checks finish.
        using var tmp = new TempDir();
        string output = tmp.WriteFile("only-good-copy.bin", "do not destroy"u8.ToArray());
        var corrupt = CraftShard("replacement.bin", "unverified"u8.ToArray(),
            expectedSha: SHA256.HashData("different"u8));

        Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([corrupt], output, _ => { }));

        Assert.Equal("do not destroy", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
    }

    [Fact]
    public void ExplicitReplacement_PreservesRestrictiveUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("private.bin", "old"u8.ToArray());
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(output, privateMode);

        new ShardAssembler().Assemble([CraftShard("private.bin", "verified replacement"u8.ToArray())],
            output, _ => { });

        Assert.Equal(privateMode, File.GetUnixFileMode(output));
        Assert.Equal("verified replacement", File.ReadAllText(output));
    }

    [Fact]
    public void ExplicitReplacement_DropsUnixSpecialPermissionBits()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("privileged.bin", "old"u8.ToArray());
        const UnixFileMode ordinary = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                      UnixFileMode.GroupExecute;
        File.SetUnixFileMode(output, ordinary | UnixFileMode.SetUser | UnixFileMode.SetGroup |
            UnixFileMode.StickyBit);

        new ShardAssembler().Assemble(
            [CraftShard("privileged.bin", "verified replacement"u8.ToArray())], output, _ => { });

        Assert.Equal(ordinary, File.GetUnixFileMode(output));
        Assert.Equal("verified replacement", File.ReadAllText(output));
    }

    [Fact]
    public void ExplicitReplacement_PreservesWindowsReadOnlyAndLeavesNoPlaintextStagingFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("private.bin", "old"u8.ToArray());
        File.SetAttributes(output, File.GetAttributes(output) | FileAttributes.ReadOnly);
        try
        {
            new ShardAssembler().Assemble(
                [CraftShard("private.bin", "verified replacement"u8.ToArray())], output, _ => { });

            Assert.Equal("verified replacement", File.ReadAllText(output));
            Assert.True((File.GetAttributes(output) & FileAttributes.ReadOnly) != 0);
            Assert.Empty(Directory.GetFiles(tmp.Path, ".private.bin.qrshard-*.tmp"));
        }
        finally
        {
            if (File.Exists(output))
                File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExplicitReplacement_PreservesMateriallyDifferentWindowsDacl()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("shared.bin", "old"u8.ToArray());
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var expected = new FileSecurity();
        expected.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        expected.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            AccessControlType.Allow));
        expected.AddAccessRule(new FileSystemAccessRule(world, FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(output).SetAccessControl(expected);

        new ShardAssembler().Assemble(
            [CraftShard("shared.bin", "verified replacement"u8.ToArray())], output, _ => { });

        FileSecurity actual = new FileInfo(output).GetAccessControl(AccessControlSections.Access);
        Assert.True(actual.AreAccessRulesProtected);
        var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.Contains(rules, rule => !rule.IsInherited && world.Equals(rule.IdentityReference) &&
            rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read);
        Assert.Equal("verified replacement", File.ReadAllText(output));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ExplicitReplacement_CompletesBeforeRestoringDaclThatDeniesWriteAttributes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("restricted.bin", "old"u8.ToArray());
        File.SetAttributes(output, File.GetAttributes(output) | FileAttributes.Hidden);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var restricted = new FileSecurity();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const FileSystemRights finalRights = FileSystemRights.ReadAndExecute | FileSystemRights.Delete;
        restricted.AddAccessRule(new FileSystemAccessRule(current, finalRights,
            AccessControlType.Allow));
        new FileInfo(output).SetAccessControl(restricted);
        string expectedDacl = new FileInfo(output).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        new ShardAssembler().Assemble(
            [CraftShard("restricted.bin", "verified replacement"u8.ToArray())], output, _ => { });

        Assert.True((File.GetAttributes(output) & FileAttributes.Hidden) != 0);
        FileSecurity actual = new FileInfo(output).GetAccessControl(AccessControlSections.Access);
        Assert.Equal(expectedDacl, actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        var rule = Assert.Single(actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>());
        Assert.Equal(current, rule.IdentityReference);
        Assert.Equal((FileSystemRights)0, rule.FileSystemRights & FileSystemRights.WriteAttributes);
        Assert.Equal("verified replacement", File.ReadAllText(output));
    }

    [Fact]
    public void FailedWindowsReadOnlyReplacement_RestoresAttributesAndDeletesPlaintextStagingFile()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string output = tmp.WriteFile("private.bin", "old"u8.ToArray());
        File.SetAttributes(output, File.GetAttributes(output) | FileAttributes.ReadOnly);
        try
        {
            using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ex = Assert.Throws<ShardDecodeException>(() => new ShardAssembler().Assemble(
                    [CraftShard("private.bin", "decrypted plaintext"u8.ToArray())], output, _ => { }));
                Assert.Contains("restore failed", ex.Message);
            }

            Assert.Equal("old", File.ReadAllText(output));
            Assert.True((File.GetAttributes(output) & FileAttributes.ReadOnly) != 0);
            Assert.Empty(Directory.GetFiles(tmp.Path, ".private.bin.qrshard-*.tmp"));
        }
        finally
        {
            if (File.Exists(output))
                File.SetAttributes(output, File.GetAttributes(output) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public void AutomaticFileName_SkipsDirectoriesAtTheBaseAndFirstFallback()
    {
        using var tmp = new TempDir();
        string cwd = Path.Combine(tmp.Path, "work");
        Directory.CreateDirectory(Path.Combine(cwd, "same.bin"));
        Directory.CreateDirectory(Path.Combine(cwd, "same.restored.bin"));

        AssembleIn(cwd, CraftShard("same.bin", "payload"u8.ToArray()));

        Assert.Equal("payload", File.ReadAllText(Path.Combine(cwd, "same.restored-2.bin")));
        Assert.True(Directory.Exists(Path.Combine(cwd, "same.bin")));
        Assert.True(Directory.Exists(Path.Combine(cwd, "same.restored.bin")));
    }

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
