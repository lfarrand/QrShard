using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Fountain-coded video mode: any enough captured frames per stripe reconstruct the data.</summary>
public class FountainTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (byte[] Content, EncodeResult Result) Encode(TempDir tmp, int size, int fountainPercent)
    {
        byte[] content = TestData.Random(size, seed: 7);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { FountainPercent = fountainPercent });
        return (content, result);
    }

    [Fact]
    public void CoefficientsAreDeterministicAndSeqDistinct()
    {
        var fec = new FountainFec();
        Assert.Equal(fec.Coefficients(42, 0, 3, 32), fec.Coefficients(42, 0, 3, 32));
        Assert.NotEqual(fec.Coefficients(42, 0, 3, 32), fec.Coefficients(42, 0, 4, 32));
        Assert.NotEqual(fec.Coefficients(42, 0, 3, 32), fec.Coefficients(42, 1, 3, 32));
        Assert.NotEqual(fec.Coefficients(42, 0, 3, 32), fec.Coefficients(43, 0, 3, 32));
    }

    [Fact]
    public void AllDataPresent_DecodesWithoutSolving()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 300_000, 100);
        Assert.True(result.DataImages >= 3);
        Assert.True(result.ParityImages >= result.DataImages); // 100% coded frames

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { });
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void AnyEnoughFrames_Reconstruct_MissingDataImages()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 300_000, 100);
        var dataFiles = result.Files.Where(f => !f.Contains("parity")).ToList();
        var codedFiles = result.Files.Where(f => f.Contains("parity")).ToList();

        // Drop HALF the data images — far beyond what Cauchy-at-100% stripes would ever plan —
        // and keep all coded frames: the equations still reach full rank.
        var survivors = dataFiles.Where((_, i) => i % 2 == 0).Concat(codedFiles).ToList();

        var log = new List<string>();
        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(survivors, output, log.Add);
        Assert.Contains(log, m => m.Contains("recovered") && m.Contains("fountain"));
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void InsufficientFrames_FailsWithActionableError()
    {
        using var tmp = new TempDir();
        var (_, result) = Encode(tmp, 300_000, 25);
        var dataFiles = result.Files.Where(f => !f.Contains("parity")).ToList();
        var codedFiles = result.Files.Where(f => f.Contains("parity")).ToList();

        // Keep too few frames overall: drop half the data but only a quarter was coded.
        var survivors = dataFiles.Where((_, i) => i % 2 == 0).Concat(codedFiles).ToList();
        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardDecoder().DecodeFolder(survivors, tmp.File("out.bin"), _ => { }));
        Assert.Contains("fountain", ex.Message);
    }

    [Fact]
    public void RecoveryAndFountain_AreMutuallyExclusive()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        Assert.Throws<ArgumentException>(() => new ShardEncoder().Encode(
            input, tmp.Sub("shards"), Fast with { RecoveryPercent = 10, FountainPercent = 10 }));
    }

    [Fact]
    public void VideoRecording_MissingDataFrames_RecoversFromCodedFrames_AndStopsEarly()
    {
        using var tmp = new TempDir();
        var (content, result) = Encode(tmp, 200_000, 100);
        var dataFiles = result.Files.Where(f => !f.Contains("parity")).ToList();
        var codedFiles = result.Files.Where(f => f.Contains("parity")).ToList();
        Assert.True(dataFiles.Count >= 2);

        // The recording never shows two of the data images; coded frames must cover them, and
        // the decoder must stop before grinding through the trailing junk cycle.
        var frames = new List<Image<Rgb24>>();
        foreach (string f in dataFiles.Where((_, i) => i >= 2).Concat(codedFiles))
            frames.Add(Image.Load<Rgb24>(f));
        int usefulFrames = frames.Count;
        foreach (string f in codedFiles) // extra cycle a naive decoder would fully demux
            frames.Add(Image.Load<Rgb24>(f));

        var root = frames[0].Clone();
        for (int i = 1; i < frames.Count; i++)
            root.Frames.AddFrame(frames[i].Frames.RootFrame);
        string recording = tmp.File("recording.png");
        root.SaveAsPng(recording);
        root.Dispose();
        frames.ForEach(f => f.Dispose());

        string output = tmp.File("out.bin");
        new VideoDecoder().Decode(recording, output, 8, _ => { }, out var stats);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.True(stats.StoppedEarly);
        Assert.True(stats.FramesExamined <= usefulFrames + 1,
            $"expected early stop by frame {usefulFrames}, examined {stats.FramesExamined}");
    }
}
