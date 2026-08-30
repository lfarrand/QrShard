using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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

    private static string DamageCopy(string source, string destPath, int x0, int y0, int size)
    {
        using var img = Image.Load<Rgb24>(source);
        for (int y = y0; y < y0 + size; y++)
            for (int x = x0; x < x0 + size; x++)
                img[x, y] = new Rgb24(128, 128, 128);
        img.SaveAsPng(destPath);
        return destPath;
    }

    [Fact]
    public void AddImage_TwoDisjointFailedCaptures_FusesAndAssembles()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("input.bin", content);
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900 });
        Assert.Equal(1, report.ImageCount);

        string capDir = tmp.Sub("captures");
        string cap1 = DamageCopy(report.Files[0], Path.Combine(capDir, "cap1.png"), 100, 100, 250);
        string cap2 = DamageCopy(report.Files[0], Path.Combine(capDir, "cap2.png"), 450, 450, 250);

        var session = new QrShardDecodeSession();
        Assert.False(session.AddImage(cap1).Accepted);
        Assert.False(session.AddImageBytes(File.ReadAllBytes(cap2), "cap2.png").Accepted);
        Assert.True(session.IsComplete);

        string output = tmp.File("out.bin");
        var restored = session.Assemble(output);
        Assert.Single(restored);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Assemble_EncryptedRightPassword_RestoresPayload()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("secret.bin", content);
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900, Password = "hunter2" });

        var session = new QrShardDecodeSession("hunter2");
        foreach (string file in report.Files)
            Assert.True(session.AddImage(file).Accepted, file);
        Assert.True(session.IsComplete);

        string output = tmp.File("out.bin");
        session.Assemble(output);
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void Assemble_EncryptedWrongPassword_ThrowsTyped()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("secret.bin", TestData.Random(5_000));
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900, Password = "right" });

        var session = new QrShardDecodeSession("wrong");
        foreach (string file in report.Files)
            Assert.True(session.AddImage(file).Accepted);

        var ex = Assert.Throws<QrShardDecodeException>(() => session.Assemble(tmp.File("out.bin")));
        Assert.Contains("wrong password", ex.Message);
    }

    [Fact]
    public void Assemble_EncryptedMissingPassword_ThrowsTyped()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("secret.bin", TestData.Random(5_000));
        var report = new QrShardCodec().EncodeFile(input, tmp.Sub("shards"),
            new QrShardEncodeOptions { Width = 900, Height = 900, Password = "pw" });

        var session = new QrShardDecodeSession();
        foreach (string file in report.Files)
            Assert.True(session.AddImage(file).Accepted);

        var ex = Assert.Throws<QrShardDecodeException>(() => session.Assemble(tmp.File("out.bin")));
        Assert.Contains("encrypted", ex.Message);
    }
}
