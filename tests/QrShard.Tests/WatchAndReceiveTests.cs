using QrShard;

namespace QrShard.Tests;

/// <summary>Watch-mode decoding and the live-receiver argument plumbing.</summary>
public class WatchAndReceiveTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    [Fact]
    public async Task WatchMode_AssemblesWhenCapturesArriveIncrementally()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3);

        string watchDir = tmp.Sub("incoming");
        string output = tmp.File("out.bin");
        string session = tmp.File("watch.qrsession");
        var stdout = new StringWriter();

        var watch = Task.Run(() => new Cli().Run(
            ["decode", watchDir, "--watch", "--session", session, "-o", output], stdout, stdout));

        // Captures arrive in two sittings, like a user screenshotting as the images cycle.
        await Task.Delay(400);
        foreach (string f in result.Files.Take(result.ImageCount / 2))
            File.Copy(f, Path.Combine(watchDir, Path.GetFileName(f)));
        await Task.Delay(1500);
        Assert.False(watch.IsCompleted); // still incomplete — must keep watching
        foreach (string f in result.Files.Skip(result.ImageCount / 2))
            File.Copy(f, Path.Combine(watchDir, Path.GetFileName(f)));

        int code = await watch.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.False(File.Exists(session)); // cleaned up on success
        Assert.Contains("Restored 1 file(s)", stdout.ToString());
    }

    [Fact]
    public void LiveInputArgs_UsePlatformFramework_AndWrapDshowNames()
    {
        Assert.Equal("-f dshow -i \"video=Integrated Camera\"",
            LiveFrameSource.BuildInputArgs("dshow", "Integrated Camera"));
        Assert.Equal("-f dshow -i \"video=USB Cam\"",
            LiveFrameSource.BuildInputArgs(null, "USB Cam") is var s && OperatingSystem.IsWindows()
                ? s
                : "-f dshow -i \"video=USB Cam\""); // platform default only checkable on Windows
        Assert.Equal("-f v4l2 -i \"/dev/video0\"", LiveFrameSource.BuildInputArgs("v4l2", "/dev/video0"));
        Assert.Equal("-f avfoundation -i \"0:none\"", LiveFrameSource.BuildInputArgs("avfoundation", "0:none"));
    }

    [Fact]
    public void Receive_OnWindowsWithoutDevice_ExplainsHowToListDevices()
    {
        if (!OperatingSystem.IsWindows())
            return; // the default-device path only errors on Windows
        var stderr = new StringWriter();
        int code = new Cli().Run(["receive"], new StringWriter(), stderr);
        Assert.Equal(2, code);
        Assert.Contains("--device", stderr.ToString());
        Assert.Contains("list_devices", stderr.ToString());
    }
}
