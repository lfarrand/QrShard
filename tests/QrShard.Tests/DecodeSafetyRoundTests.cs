using QrShard;

namespace QrShard.Tests;

/// <summary>The "decode success + safety" round: option validation, dry-run, graceful incomplete
/// decode, the roundtrip self-test, and 3-finder pose reconstruction.</summary>
public class DecodeSafetyRoundTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli().Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // ---- Item 1: reject unknown options ----

    [Fact]
    public void TypoedPasswordFlag_IsRejected_NotSilentlyUnencrypted()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(1000));
        var (code, _, err) = Run("encode", input, "-o", tmp.File("s"), "-r", "900", "--pasword", "secret");
        Assert.Equal(2, code);
        Assert.Contains("unknown option '--pasword'", err);
        Assert.Contains("--password", err); // did-you-mean hint
        Assert.False(Directory.Exists(tmp.File("s"))); // nothing encoded
    }

    [Fact]
    public void WrongCommandFlag_IsRejected()
    {
        using var tmp = new TempDir();
        var (code, _, err) = Run("decode", tmp.Sub("shards"), "--camera"); // --camera is an encode flag
        Assert.Equal(2, code);
        Assert.Contains("unknown option '--camera'", err);
    }

    [Fact]
    public void OptionMissingItsValue_IsDetected()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(1000));
        var (code, _, err) = Run("encode", input, "-o", tmp.File("s"), "-r", "900", "--recovery", "--camera");
        Assert.Equal(2, code);
        Assert.Contains("missing a value", err);
    }

    [Fact]
    public void ValidOptions_StillAccepted()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(2000));
        var (code, _, _) = Run("encode", input, "-o", tmp.File("s"), "-r", "900", "-c", "3", "-b", "4", "-e", "16", "-R", "10");
        Assert.Equal(0, code);
    }

    // ---- Item 4: encode --dry-run ----

    [Fact]
    public void DryRun_PrintsCount_WritesNothing()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(200_000));
        string shards = tmp.File("s");
        var (code, output, _) = Run("encode", input, "-o", shards, "-r", "900", "--dry-run");
        Assert.Equal(0, code);
        Assert.Contains("Dry run", output);
        Assert.Contains("image(s)", output);
        Assert.False(Directory.Exists(shards)); // no images written
    }

    [Fact]
    public void DryRun_Json_MatchesRealEncodeCount()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(200_000));
        var (_, dry, _) = Run("encode", input, "-o", tmp.File("s"), "-r", "900", "-R", "20", "--dry-run", "--json");
        using var doc = System.Text.Json.JsonDocument.Parse(dry);
        int plannedImages = doc.RootElement.GetProperty("imageCount").GetInt32();
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());

        // The dry-run count must equal what a real encode actually produces.
        var report = new ShardEncoder().Encode(input, tmp.Sub("real"),
            new EncodeOptions { Width = 900, Height = 900, RecoveryPercent = 20 });
        Assert.Equal(report.ImageCount, plannedImages);
    }

    // ---- Item 5: graceful incomplete decode ----

    [Fact]
    public void IncompleteDecode_ShowsStatus_PointsToSession_ExitCode3()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(200_000);
        string input = tmp.WriteFile("in.bin", content);
        string shards = tmp.File("s");
        Assert.Equal(0, Run("encode", input, "-o", shards, "-r", "900").Code);

        // Remove one data image → the set is incomplete but not corrupt.
        string firstData = Directory.GetFiles(shards, "*.png").First(f => !f.Contains("parity"));
        File.Delete(firstData);

        var (code, output, _) = Run("decode", shards, "-o", tmp.File("out.bin"));
        Assert.Equal(3, code); // documented "incomplete" exit code
        Assert.Contains("missing image", output);
        Assert.Contains("--session", output);
    }

    // ---- Item 6: roundtrip self-test on the user's own file ----

    [Fact]
    public void RoundtripTest_OnComfortableSettings_Passes()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("mine.bin", TestData.Random(60_000));
        var (code, output, _) = Run("test", input, "-r", "900");
        Assert.Equal(0, code);
        Assert.Contains("survives", output);
        Assert.Contains("ECC used", output);
    }

    [Fact]
    public void RoundtripTest_OnTooDenseSettings_FailsWithAdvice()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("mine.bin", TestData.Random(60_000));
        var (code, output, _) = Run("test", input, "-r", "900", "-c", "1", "-b", "8", "-e", "0");
        Assert.Equal(1, code);
        Assert.Contains("did NOT survive", output);
    }

    // ---- Item 2: 3-finder pose reconstruction ----

    private static FinderCluster Cluster(double x, double y, double module, int count) =>
        new() { SumX = x * count, SumY = y * count, SumModule = module * count, Count = count };

    [Fact]
    public void ChooseQuad_ThreeFindersRightAngle_ReconstructsFourthCorner()
    {
        // Right angle at (0,0); the fourth corner should be the parallelogram completion (100,100).
        var clusters = new List<FinderCluster>
        {
            Cluster(0, 0, 5, 3), Cluster(100, 0, 5, 3), Cluster(0, 100, 5, 3),
        };
        var quad = new QuadSelector().ChooseQuad(clusters);
        Assert.NotNull(quad);
        Assert.Contains(quad.Points, p => Math.Abs(p.X - 100) < 1 && Math.Abs(p.Y - 100) < 1);
    }

    [Fact]
    public void ChooseQuad_ThreeCollinearFinders_Rejected()
    {
        // No right angle → cannot reconstruct a rectangle; must refuse (CRC would reject anyway).
        var clusters = new List<FinderCluster>
        {
            Cluster(0, 0, 5, 3), Cluster(100, 0, 5, 3), Cluster(200, 0, 5, 3),
        };
        Assert.Null(new QuadSelector().ChooseQuad(clusters));
    }

    [Fact]
    public void ChooseQuad_TwoFinders_StillRejected()
    {
        var clusters = new List<FinderCluster> { Cluster(0, 0, 5, 3), Cluster(100, 0, 5, 3) };
        Assert.Null(new QuadSelector().ChooseQuad(clusters));
    }
}
