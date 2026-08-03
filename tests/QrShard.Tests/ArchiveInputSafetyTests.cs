using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using QrShard;

namespace QrShard.Tests;

public class ArchiveInputSafetyTests
{
    [Fact]
    public void FolderEnumeration_DoesNotFollowLinksOutsideTheSelectedTree()
    {
        using var tmp = new TempDir();
        string selected = tmp.Sub("selected");
        string outside = tmp.Sub("outside");
        File.WriteAllText(Path.Combine(selected, "ordinary.txt"), "included");
        File.WriteAllText(Path.Combine(selected, ".hidden.txt"), "included too");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "must not leak");

        string link = Path.Combine(selected, "linked-outside");
        CreateDirectoryLink(link, outside);

        var relative = Cli.EnumerateArchiveFiles(selected)
            .Select(path => Path.GetRelativePath(selected, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path)
            .ToList();

        Assert.Equal([".hidden.txt", "ordinary.txt"], relative);
    }

    [Fact]
    public void FolderRoundTrip_PreservesNestedEmptyDirectories()
    {
        using var tmp = new TempDir();
        string input = tmp.Sub("source");
        Directory.CreateDirectory(Path.Combine(input, "empty", "nested"));
        Directory.CreateDirectory(Path.Combine(input, "not-empty"));
        File.WriteAllText(Path.Combine(input, "not-empty", "file.txt"), "content");
        string shards = tmp.File("shards");
        string restored = tmp.File("restored");

        Assert.Equal(0, new Cli().Run(["encode", input, "-o", shards, "-r", "900"],
            new StringWriter(), new StringWriter(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, new Cli().Run(["decode", shards, "-o", restored],
            new StringWriter(), new StringWriter(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(Path.Combine(restored, "empty", "nested")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(restored, "not-empty", "file.txt")));
    }

    [Fact]
    public void TopLevelDirectoryLink_IsRejectedInsteadOfDereferenced()
    {
        using var tmp = new TempDir();
        string target = tmp.Sub("target");
        File.WriteAllText(Path.Combine(target, "secret.txt"), "secret");
        string link = tmp.File("selected-link");
        CreateDirectoryLink(link, target);

        var error = new StringWriter();
        int exit = new Cli().Run(["encode", link, "-o", tmp.File("shards")],
            new StringWriter(), error, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, exit);
        Assert.Contains("symbolic links and junctions", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HardLinkedFiles_RoundTripAsOrdinaryFileContents()
    {
        using var tmp = new TempDir();
        string input = tmp.Sub("hardlinks");
        string first = Path.Combine(input, "first.txt");
        string second = Path.Combine(input, "second.txt");
        File.WriteAllText(first, "same inode, two selected paths");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(first, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        CreateHardLink(second, first);
        string shards = tmp.File("shards");
        string restored = tmp.File("restored");

        Assert.Equal(0, new Cli().Run(["encode", input, "-o", shards, "-r", "900"],
            new StringWriter(), new StringWriter(), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, new Cli().Run(["decode", shards, "-o", restored],
            new StringWriter(), new StringWriter(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("same inode, two selected paths", File.ReadAllText(Path.Combine(restored, "first.txt")));
        Assert.Equal("same inode, two selected paths", File.ReadAllText(Path.Combine(restored, "second.txt")));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(restored, "first.txt")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void PlaintextArchiveTempDirectory_IsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temp = Cli.CreatePrivateTempDirectory();
        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(temp.Path) & ~ownerOnly);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PlaintextArchiveTempDirectory_HasProtectedOwnerOnlyWindowsAcl()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using (var temp = Cli.CreatePrivateTempDirectory())
        {
            string path = temp.Path;
            DirectorySecurity acl = new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(acl.AreAccessRulesProtected);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier current = identity.User!;
            Assert.Equal(current, acl.GetOwner(typeof(SecurityIdentifier)));
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
            Assert.Throws<IOException>(() => ShardAssembler.CreatePrivateWindowsDirectory(path));
            Exception? rename = Record.Exception(() => Directory.Move(path, path + "-moved"));
            Assert.True(rename is IOException or UnauthorizedAccessException,
                $"leased directory was unexpectedly renameable: {rename?.GetType().Name ?? "no error"}");
            Assert.True(Directory.Exists(path));
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsLease_PublishesTheExactDirectoryByHandleAndRemainsPinned()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? published = null;
        using (var temp = Cli.CreatePrivateTempDirectory())
        {
            string source = temp.Path;
            published = source + "-published";
            File.WriteAllText(Path.Combine(source, "proof.txt"), "verified tree");

            temp.MoveTo(published);

            Assert.False(Directory.Exists(source));
            Assert.Equal("verified tree", File.ReadAllText(Path.Combine(published, "proof.txt")));
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "foreign.txt"), "must survive lease disposal");
            Exception? rename = Record.Exception(() => Directory.Move(published, published + "-swapped"));
            Assert.True(rename is IOException or UnauthorizedAccessException,
                $"published leased directory was unexpectedly renameable: {rename?.GetType().Name ?? "no error"}");
        }

        // MoveTo publishes rather than transferring cleanup ownership: the caller's output must
        // survive lease disposal, and a replacement at the now-free old staging pathname must not
        // be mistaken for the leased object either.
        Assert.True(Directory.Exists(published));
        string recreatedSource = published![..^"-published".Length];
        Assert.Equal("must survive lease disposal",
            File.ReadAllText(Path.Combine(recreatedSource, "foreign.txt")));
        Directory.Delete(published!, recursive: true);
        Directory.Delete(recreatedSource, recursive: true);
    }

    [Fact]
    public void ArchiveCreation_StripsUnixSpecialBitsFromTarMetadata()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string input = tmp.Sub("source");
        string source = Path.Combine(input, "executable");
        File.WriteAllText(source, "content");
        const UnixFileMode ordinary = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                      UnixFileMode.OtherRead;
        File.SetUnixFileMode(source, ordinary | UnixFileMode.SetUser | UnixFileMode.SetGroup |
            UnixFileMode.StickyBit);
        string tar = tmp.File("payload.tar");

        Cli.WriteTar([input], tar);

        using var stream = File.OpenRead(tar);
        using var reader = new TarReader(stream);
        TarEntry entry = Assert.IsType<PaxTarEntry>(reader.GetNextEntry());
        Assert.Equal("executable", entry.Name);
        Assert.Equal(ordinary, entry.Mode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tar));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // Directory.CreateSymbolicLink needs SeCreateSymbolicLinkPrivilege on Windows. A junction
        // exercises the same reparse-point behaviour without requiring an elevated test runner.
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"could not create test junction: {stdout} {stderr}");
    }

    private static void CreateHardLink(string link, string target)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "ln",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/H");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
        }
        else
        {
            start.ArgumentList.Add(target);
            start.ArgumentList.Add(link);
        }
        using var process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"could not create test hard link: {stdout} {stderr}");
    }
}
