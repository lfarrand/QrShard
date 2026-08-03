using System.Diagnostics;
using QrShard;

namespace QrShard.Tests;

[Collection(CurrentDirectoryCollection.Name)]
public sealed class ExternalToolSecurityTests
{
    [Fact]
    public void PathResolutionSkipsCurrentDirectoryPlanting()
    {
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string trusted = tmp.Sub("trusted");
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string planted = MakeExecutable(Path.Combine(working, name));
        string expected = MakeExecutable(Path.Combine(trusted, name));
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, working, trusted));

            Assert.Equal(PhysicalPath.Canonicalize(expected), ExternalToolResolver.Resolve("ffmpeg"));
            Assert.NotEqual(PhysicalPath.Canonicalize(planted), ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void RelativeAndEmptyPathEntriesCannotSelectAPlantedExecutable()
    {
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        MakeExecutable(Path.Combine(working, name));
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, "", ".", "relative"));
            Assert.Null(ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void ExplicitExecutableMustBeAbsoluteButMayBeDeliberatelySelected()
    {
        using var tmp = new TempDir();
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string executable = MakeExecutable(tmp.File(name));

        Assert.Equal(PhysicalPath.Canonicalize(executable), ExternalToolResolver.Resolve("ffmpeg", executable));
        Assert.Throws<InvalidOperationException>(() => ExternalToolResolver.Resolve("ffmpeg", name));
    }

    [Fact]
    public void PathResolutionSkipsPhysicalAliasesOfCurrentDirectoryAndChildren()
    {
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string child = Directory.CreateDirectory(Path.Combine(working, "child")).FullName;
        string trusted = tmp.Sub("trusted");
        string cwdAlias = tmp.File("cwd-alias");
        string childAlias = tmp.File("child-alias");
        CreateDirectoryLink(cwdAlias, working);
        CreateDirectoryLink(childAlias, child);
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        MakeExecutable(Path.Combine(working, name));
        MakeExecutable(Path.Combine(child, name));
        string expected = MakeExecutable(Path.Combine(trusted, name));
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH",
                string.Join(Path.PathSeparator, cwdAlias, childAlias, trusted));

            Assert.Equal(PhysicalPath.Canonicalize(expected), ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void PathResolutionRestartsWhenALinkTargetContainsAnotherLink()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string child = Directory.CreateDirectory(Path.Combine(working, "child")).FullName;
        string trusted = tmp.Sub("trusted");
        string parentAlias = tmp.File("parent-alias");
        string nestedAlias = tmp.File("nested-alias");
        Directory.CreateSymbolicLink(parentAlias, "working");
        Directory.CreateSymbolicLink(nestedAlias, Path.Combine("parent-alias", "child"));
        string name = "ffmpeg";
        MakeExecutable(Path.Combine(child, name));
        string expected = MakeExecutable(Path.Combine(trusted, name));
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, nestedAlias, trusted));

            Assert.Equal(PhysicalPath.Canonicalize(expected), ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void ExecutableSymlinkIntoCurrentDirectoryIsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string linkBin = tmp.Sub("link-bin");
        string trusted = tmp.Sub("trusted");
        string planted = MakeExecutable(Path.Combine(working, "ffmpeg"));
        File.CreateSymbolicLink(Path.Combine(linkBin, "ffmpeg"), planted);
        string expected = MakeExecutable(Path.Combine(trusted, "ffmpeg"));
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, linkBin, trusted));

            Assert.Equal(PhysicalPath.Canonicalize(expected), ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void ExecutableSymlinkToASafeDirectoryIsAcceptedAsItsPhysicalTarget()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string working = tmp.Sub("working");
        string linkBin = tmp.Sub("link-bin");
        string realBin = tmp.Sub("real-bin");
        string executable = MakeExecutable(Path.Combine(realBin, "ffmpeg"));
        File.CreateSymbolicLink(Path.Combine(linkBin, "ffmpeg"), executable);
        string priorDirectory = Environment.CurrentDirectory;
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = working;
            Environment.SetEnvironmentVariable("PATH", linkBin);

            Assert.Equal(PhysicalPath.Canonicalize(executable), ExternalToolResolver.Resolve("ffmpeg"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
            Environment.CurrentDirectory = priorDirectory;
        }
    }

    [Fact]
    public void PathResolutionPreservesNormalizationSensitiveFileSystemNames()
    {
        using var tmp = new TempDir();
        string decomposedDirectory = tmp.Sub("cafe\u0301-bin");
        string composedDirectory = tmp.File("caf\u00e9-bin");
        if (Directory.Exists(composedDirectory))
            return; // This mounted filesystem cannot represent the distinction under test.
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string executable = MakeExecutable(Path.Combine(decomposedDirectory, name));
        string expected = PhysicalPath.Canonicalize(executable);
        Assert.Equal("cafe\u0301-bin", Path.GetFileName(Path.GetDirectoryName(expected)));
        string? priorPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", decomposedDirectory);

            Assert.Equal(expected, ExternalToolResolver.Resolve("ffmpeg"));
            Assert.Equal(expected, ExternalToolResolver.Resolve("ffmpeg", executable));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", priorPath);
        }
    }

    [Fact]
    public void ChildProcessUsesAbsoluteExecutableAndRestrictedWorkingDirectory()
    {
        using var tmp = new TempDir();
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string executable = MakeExecutable(tmp.File(name));

        var start = ExternalToolResolver.CreateStartInfo(executable);

        Assert.Equal(Path.GetFullPath(executable), start.FileName);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(executable)), start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.StartsWith(start.WorkingDirectory, start.Environment["PATH"],
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserLauncherUsesResolvedAbsoluteExecutableAndOnePathArgument()
    {
        using var tmp = new TempDir();
        string executable = MakeExecutable(tmp.File(OperatingSystem.IsWindows() ? "xdg-open.exe" : "xdg-open"));
        string slideshow = tmp.File("slide show.html");
        string? resolvedName = null;
        string? configured = "unexpected";

        var start = Cli.BuildBrowserStartInfo(slideshow, isWindows: false, isMacOS: false,
            (name, configuredPath) =>
            {
                resolvedName = name;
                configured = configuredPath;
                return executable;
            });

        Assert.NotNull(start);
        Assert.Equal("xdg-open", resolvedName);
        Assert.Null(configured);
        Assert.Equal(Path.GetFullPath(executable), start.FileName);
        Assert.Equal([Path.GetFullPath(slideshow)], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void MacBrowserLauncherPinsSystemOpenInsteadOfUsingBareName()
    {
        using var tmp = new TempDir();
        string executable = MakeExecutable(tmp.File(OperatingSystem.IsWindows() ? "open.exe" : "open"));
        string? configured = null;

        var start = Cli.BuildBrowserStartInfo(tmp.File("show.html"), isWindows: false, isMacOS: true,
            (_, configuredPath) =>
            {
                configured = configuredPath;
                return executable;
            });

        Assert.NotNull(start);
        Assert.Equal("/usr/bin/open", configured);
        Assert.Equal(Path.GetFullPath(executable), start.FileName);
    }

    [Fact]
    public async Task StderrDrainBoundsOneHugeLineFromAHelperProcess()
    {
        const string block = "abcdefghijklmno ";
        const int retainedChars = 4096;
        ProcessStartInfo start = LongStderrHelper(block, repetitions: 4096);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using Process helper = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the fake stderr helper.");

        Task<string> draining = RecordingFrameSource.ReadBoundedErrorTailAsync(
            helper.StandardError, retainedChars, timeout.Token);
        await helper.WaitForExitAsync(timeout.Token);
        string tail = await draining.WaitAsync(timeout.Token);

        Assert.Equal(retainedChars, tail.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat(block, retainedChars / block.Length)), tail);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(257)]
    public async Task StderrDrainKeepsTheExactTailAcrossArbitraryReadBoundaries(int readSize)
    {
        string input = string.Concat(Enumerable.Range(0, 10_000)
            .Select(static i => (char)('a' + i % 26)));
        using var reader = new ChunkedTextReader(input, readSize);

        string tail = await RecordingFrameSource.ReadBoundedErrorTailAsync(reader, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(input[^4096..], tail);
    }

    [Fact]
    public void StderrDrainCompletionIsBoundedWhenAReaderIgnoresCancellation()
    {
        var neverCompletes = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();

        string tail = RecordingFrameSource.CompleteErrorDrain(
            neverCompletes.Task, cancellation, milliseconds: 25);

        elapsed.Stop();
        Assert.Empty(tail);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private static ProcessStartInfo LongStderrHelper(string block, int repetitions)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            string command = $"for /L %i in (1,1,{repetitions}) do @<nul set /p x={block}1>&2";
            start = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command);
        }
        else
        {
            string command = $"i=0; while [ $i -lt {repetitions} ]; do printf '{block}' >&2; i=$((i+1)); done";
            start = new ProcessStartInfo("/bin/sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
        }
        start.UseShellExecute = false;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        return start;
    }

    private static string MakeExecutable(string path)
    {
        File.WriteAllBytes(path, [0]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.NotEmpty(system);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(system, "cmd.exe"),
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

    private sealed class ChunkedTextReader(string text, int chunkSize) : TextReader
    {
        private int offset;

        public override ValueTask<int> ReadAsync(Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = Math.Min(Math.Min(chunkSize, buffer.Length), text.Length - offset);
            if (take == 0)
                return ValueTask.FromResult(0);
            text.AsMemory(offset, take).CopyTo(buffer);
            offset += take;
            return ValueTask.FromResult(take);
        }
    }
}
