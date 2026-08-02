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

            Assert.Equal(Path.GetFullPath(expected), ExternalToolResolver.Resolve("ffmpeg"));
            Assert.NotEqual(Path.GetFullPath(planted), ExternalToolResolver.Resolve("ffmpeg"));
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

        Assert.Equal(Path.GetFullPath(executable), ExternalToolResolver.Resolve("ffmpeg", executable));
        Assert.Throws<InvalidOperationException>(() => ExternalToolResolver.Resolve("ffmpeg", name));
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

        string tail = await RecordingFrameSource.ReadBoundedErrorTailAsync(reader, 4096);

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
