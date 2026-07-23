using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Session accumulation across decode runs, the verify command, and the ECC heatmap.</summary>
public class SessionAndDiagnosticsTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (int Code, string Out) Run(params string[] args)
    {
        var stdout = new StringWriter();
        int code = new Cli().Run(args, stdout, stdout);
        return (code, stdout.ToString());
    }

    [Fact]
    public void SessionDecode_AccumulatesAcrossRuns_ThenAssembles()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3);

        // First sitting: only the first image was captured.
        string cap1 = tmp.Sub("cap1");
        File.Copy(result.Files[0], Path.Combine(cap1, Path.GetFileName(result.Files[0])));
        string session = tmp.File("transfer.qrsession");
        string output = tmp.File("out.bin");

        var (code1, out1) = Run("decode", cap1, "--session", session, "-o", output);
        Assert.Equal(3, code1); // incomplete, but valid
        Assert.Contains("Set incomplete", out1);
        Assert.Contains("missing image(s) 2", out1);
        Assert.True(File.Exists(session));

        // Second sitting: the remaining images.
        string cap2 = tmp.Sub("cap2");
        foreach (string f in result.Files.Skip(1))
            File.Copy(f, Path.Combine(cap2, Path.GetFileName(f)));

        var (code2, out2) = Run("decode", cap2, "--session", session, "-o", output);
        Assert.Equal(0, code2);
        Assert.Contains("resuming with 1 previously collected shard", out2);
        Assert.Contains("SHA-256 verified", out2);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.False(File.Exists(session)); // session cleaned up on success
    }

    [Fact]
    public void SessionFile_SurvivesReloadWithPayloadIntact()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(40_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        var shards = new ShardDecoder().CollectShards(result.Files, _ => { });

        string session = tmp.File("s.qrsession");
        var store = new SessionStore();
        store.Save(session, shards);
        var loaded = store.Load(session);

        Assert.Equal(shards.Count, loaded.Count);
        for (int i = 0; i < shards.Count; i++)
        {
            Assert.Equal(shards[i].Header.FileId, loaded[i].Header.FileId);
            Assert.Equal(shards[i].Header.Index, loaded[i].Header.Index);
            Assert.Equal(shards[i].Payload, loaded[i].Payload);
        }
    }

    [Fact]
    public void SessionLoad_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new SessionStore().Load(Path.Combine(Path.GetTempPath(), "does-not-exist.qrsession")));
    }

    [Fact]
    public void Verify_ReportsCompleteAndIncompleteSets()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        var (codeComplete, outComplete) = Run("verify", tmp.File("shards"));
        Assert.Equal(0, codeComplete);
        Assert.Contains("Complete", outComplete);

        File.Delete(result.Files[1]);
        var (codeIncomplete, outIncomplete) = Run("verify", tmp.File("shards"));
        Assert.Equal(1, codeIncomplete);
        Assert.Contains("missing image(s) 2", outIncomplete);
    }

    [Fact]
    public void Verify_ParityCoveredLoss_ReportsRecoverable()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { RecoveryPercent = 25 });
        File.Delete(result.Files.First(f => !f.Contains("parity")));

        var (code, output) = Run("verify", tmp.File("shards"));
        Assert.Equal(0, code);
        Assert.Contains("recoverable", output);
    }

    [Fact]
    public void Heatmap_CleanImage_RendersAllGreen()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(20_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        string heatmap = tmp.File("heat.png");
        var (code, output) = Run("info", result.Files[0], "--heatmap", heatmap);
        Assert.Equal(0, code);
        Assert.Contains("heatmap", output);
        Assert.Contains("0 codeword(s) needed correction", output);

        using var img = Image.Load<Rgb24>(heatmap);
        Assert.True(img.Width > 0 && img.Height > 0);
        var p = img[img.Width / 2, img.Height / 2];
        Assert.True(p.G > p.R, $"expected green-dominant clean cells, got {p}"); // clean = green
    }

    [Fact]
    public void Heatmap_DamagedImage_ShowsCorrections()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(20_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        // Scribble a block over the data area, well inside ECC capacity for a localized blob.
        using (var img = Image.Load<Rgb24>(result.Files[0]))
        {
            for (int y = 420; y < 440; y++)
                for (int x = 420; x < 440; x++)
                    img[x, y] = new Rgb24(128, 128, 128);
            img.SaveAsPng(result.Files[0]);
        }

        string heatmap = tmp.File("heat.png");
        var (code, output) = Run("info", result.Files[0], "--heatmap", heatmap);
        Assert.Equal(0, code);
        Assert.DoesNotContain("0 codeword(s) needed correction", output);
        Assert.True(File.Exists(heatmap));
    }

    [Fact]
    public void Heatmap_NoEcc_FallsBackToQualityMap()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(5_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { EccParity = 0 });

        // With no ECC there is no correction map, but --heatmap now falls back to the
        // capture-quality map (per-cell classification confidence) instead of erroring.
        string heat = tmp.File("heat.png");
        var (code, output) = Run("info", result.Files[0], "--heatmap", heat);
        Assert.Equal(0, code);
        Assert.Contains("capture-quality map", output);
        Assert.True(File.Exists(heat));
    }
}
