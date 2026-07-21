using QrShard;

namespace QrShard.Tests;

/// <summary>The public incremental QrShardDecodeSession.</summary>
public class DecodeSessionTests
{
    [Fact]
    public void Session_AccumulatesImages_AndAssemblesWhenComplete()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900 });
        Assert.True(report.ImageCount >= 3);

        var session = new QrShardDecodeSession();
        for (int i = 0; i < report.Files.Count; i++)
        {
            Assert.False(session.IsComplete); // not complete until the last image
            var result = session.AddImage(report.Files[i]);
            Assert.True(result.Accepted, result.Error);
            Assert.True(result.WasNew);

            var status = session.Status();
            Assert.Single(status);
            Assert.Equal(i + 1, status[0].DataPresent);
            Assert.Equal(report.ImageCount, status[0].DataTotal);
        }

        Assert.True(session.IsComplete);
        string output = tmp.File("out.bin");
        var restored = session.Assemble(output);
        Assert.Single(restored);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Session_DeduplicatesRepeatedCaptures()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(30_000));
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900 });

        var session = new QrShardDecodeSession();
        Assert.True(session.AddImage(report.Files[0]).WasNew);
        var again = session.AddImage(report.Files[0]);
        Assert.True(again.Accepted);
        Assert.False(again.WasNew); // duplicate — accepted but not counted twice
        Assert.Equal(1, session.Status()[0].DataPresent);
    }

    [Fact]
    public void Session_ReportsMissingImages_AndParityRecoverability()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900, RecoveryPercent = 25 });
        var dataFiles = report.Files.Where(f => !f.Contains("parity")).ToList();
        var parityFiles = report.Files.Where(f => f.Contains("parity")).ToList();

        var session = new QrShardDecodeSession();
        // Add all but the first data image.
        foreach (string f in dataFiles.Skip(1))
            session.AddImage(f);
        var status = session.Status();
        Assert.Contains(0, status[0].MissingImages); // data image index 0 (zero-based) missing
        Assert.False(status[0].Recoverable);         // no parity yet
        Assert.False(session.IsComplete);

        // Adding parity makes it recoverable without the missing data image.
        foreach (string f in parityFiles)
            session.AddImage(f);
        Assert.True(session.IsComplete);
        string output = tmp.File("out.bin");
        session.Assemble(output);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Session_AddImageBytes_DecodesInMemory()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900 });

        var session = new QrShardDecodeSession();
        foreach (string f in report.Files)
        {
            var bytes = File.ReadAllBytes(f);
            Assert.True(session.AddImageBytes(bytes, Path.GetFileName(f)).Accepted);
        }
        Assert.True(session.IsComplete);
        string output = tmp.File("out.bin");
        session.Assemble(output);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Session_RejectsGarbageImage_WithoutThrowing()
    {
        var session = new QrShardDecodeSession();
        var result = session.AddImageBytes(TestData.Random(500), "garbage");
        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public void Session_AssembleBeforeComplete_ThrowsTyped()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900 });
        var session = new QrShardDecodeSession();
        session.AddImage(report.Files[0]); // only one of several
        Assert.False(session.IsComplete);
        Assert.Throws<QrShardDecodeException>(() => session.Assemble(tmp.File("out.bin")));
    }
}
