using System.Text.Json;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// `decode --json`: the scripting surface. A caller that cannot see the terminal needs the
/// resolved output paths (which it cannot predict — see <see cref="DecodeJsonOutputPathTests"/>),
/// an exit code that separates "capture more" from "these images are unusable", and a stdout
/// that parses without pre-filtering the progress log out of it.
/// </summary>
public class DecodeJsonTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli().Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void DecodeJson_IsPureJson_AndReportsEachRestoredFile()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(40_000);
        string input = tmp.WriteFile("payload.bin", content);
        string shards = tmp.Sub("shards");
        new ShardEncoder().Encode(input, shards, Fast);

        string restored = tmp.File("restored.bin");
        var (code, output, _) = Run("decode", shards, "-o", restored, "--json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output); // throws if a progress line leaked onto stdout
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        var file = Assert.Single(doc.RootElement.GetProperty("restored").EnumerateArray());
        Assert.Equal("payload.bin", file.GetProperty("fileName").GetString());
        Assert.Equal(restored, file.GetProperty("outputPath").GetString());
        Assert.Equal(content.Length, file.GetProperty("length").GetInt64());
        // The reported path is the real one, not a plausible-looking guess.
        Assert.Equal(content, File.ReadAllBytes(file.GetProperty("outputPath").GetString()!));
    }

    [Fact]
    public void DecodeJson_ArchivePayload_ReportsTheExtractionDirectory()
    {
        // An archive restores to a DIRECTORY, not a file — a script that assumed otherwise would
        // look for the wrong thing entirely, so outputPath must name what was actually produced.
        using var tmp = new TempDir();
        byte[] a = TestData.Random(9_000, 1);
        string fa = tmp.WriteFile("a.bin", a);
        string fb = tmp.WriteFile("b.bin", TestData.Random(4_000, 2));
        string shards = tmp.File("shards");
        Assert.Equal(0, Run("encode", fa, fb, "-o", shards, "-r", "900").Code);

        string dest = tmp.File("restored");
        var (code, output, _) = Run("decode", shards, "-o", dest, "--json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        string path = doc.RootElement.GetProperty("restored")[0].GetProperty("outputPath").GetString()!;
        Assert.Equal(dest, path);
        Assert.True(Directory.Exists(path));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(path, "a.bin")));
    }

    [Fact]
    public void DecodeJson_IncompleteSet_ReportsMissingImagesAndExitsThree()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3);
        File.Delete(result.Files[1]);

        var (code, output, _) = Run("decode", tmp.File("shards"), "-o", tmp.File("out.bin"), "--json");

        Assert.Equal(3, code);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("complete").GetBoolean());
        var file = doc.RootElement.GetProperty("files")[0];
        Assert.Equal("input.bin", file.GetProperty("fileName").GetString());
        Assert.Equal(result.ImageCount, file.GetProperty("dataTotal").GetInt32());
        Assert.False(file.GetProperty("recoverable").GetBoolean());
        Assert.Equal(2, file.GetProperty("missing")[0].GetInt32());
        // No "restored": Assemble writes any complete file before throwing on an incomplete
        // sibling and that list does not survive the throw, so the key would have to be a guess.
        Assert.False(doc.RootElement.TryGetProperty("restored", out _));
    }

    [Fact]
    public void DecodeJson_IncompleteSet_IsByteForByteTheVerifyShape()
    {
        // One shape for "what is missing", whichever command asked. If these ever diverge, a
        // script written against verify silently mis-reads decode.
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        File.Delete(result.Files[1]);

        var (decodeCode, decodeOut, _) = Run("decode", tmp.File("shards"), "-o", tmp.File("out.bin"), "--json");
        var (verifyCode, verifyOut, _) = Run("verify", tmp.File("shards"), "--json");

        Assert.Equal(3, decodeCode);
        Assert.Equal(3, verifyCode);
        Assert.Equal(verifyOut, decodeOut);
    }

    [Fact]
    public void DecodeJson_ParityRebuiltImage_StillReportsComplete()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast with { RecoveryPercent = 25 });
        File.Delete(result.Files.First(f => !f.Contains("parity")));

        string restored = tmp.File("out.bin");
        var (code, output, _) = Run("decode", tmp.File("shards"), "-o", restored, "--json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void DecodeJson_Session_ReportsIncompleteThenRestored()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000);
        string input = tmp.WriteFile("input.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(result.ImageCount >= 3);

        string cap1 = tmp.Sub("cap1");
        File.Copy(result.Files[0], Path.Combine(cap1, Path.GetFileName(result.Files[0])));
        string session = tmp.File("t.qrsession");
        string output = tmp.File("out.bin");

        var (code1, out1, _) = Run("decode", cap1, "--session", session, "-o", output, "--json");
        Assert.Equal(3, code1);
        using (var doc1 = JsonDocument.Parse(out1)) // the session log must not pollute stdout either
            Assert.False(doc1.RootElement.GetProperty("complete").GetBoolean());
        Assert.True(File.Exists(session));

        string cap2 = tmp.Sub("cap2");
        foreach (string f in result.Files.Skip(1))
            File.Copy(f, Path.Combine(cap2, Path.GetFileName(f)));

        var (code2, out2, _) = Run("decode", cap2, "--session", session, "-o", output, "--json");
        Assert.Equal(0, code2);
        using var doc2 = JsonDocument.Parse(out2);
        Assert.True(doc2.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(output, doc2.RootElement.GetProperty("restored")[0].GetProperty("outputPath").GetString());
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void DecodeJson_Recording_ReportsRestoredFiles()
    {
        // The video path assembles from frames rather than a folder, and reports through the same
        // writer — the JSON must not depend on how the shards were captured.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(120_000);
        string input = tmp.WriteFile("input.bin", content);
        string shards = tmp.File("shards");
        Assert.Equal(0, Run("encode", input, "-o", shards, "-r", "900", "--video", "--slideshow", "apng").Code);

        string restored = tmp.File("out.bin");
        var (code, output, _) = Run("decode", Path.Combine(shards, "slideshow.apng"), "-o", restored, "--json");

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(restored, doc.RootElement.GetProperty("restored")[0].GetProperty("outputPath").GetString());
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void DecodeJson_UndecodableImages_KeepStdoutEmptyAndReportOnStderr()
    {
        // A hard error must not emit half a document: a script parsing stdout unconditionally
        // would otherwise choke on the error text rather than see clean JSON-or-nothing.
        using var tmp = new TempDir();
        string junk = tmp.Sub("junk");
        File.WriteAllBytes(Path.Combine(junk, "not-a-shard.png"), TestData.Random(2_000));

        var (code, output, err) = Run("decode", junk, "--json");

        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("no decodable shard images", err);
    }

    [Fact]
    public void DecodeHumanOutput_IsUnchangedWithoutTheFlag()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("payload.bin", TestData.Random(40_000));
        string shards = tmp.Sub("shards");
        var result = new ShardEncoder().Encode(input, shards, Fast);

        var (code, output, _) = Run("decode", shards, "-o", tmp.File("r.bin"));

        Assert.Equal(0, code);
        Assert.Contains($"Decoding {result.ImageCount} image(s)...", output);
        Assert.Contains("SHA-256 verified", output);
        Assert.Contains("Restored 1 file(s).", output);
        Assert.DoesNotContain("{", output);
    }
}

/// <summary>
/// The output path with no -o: derived from the shard header, in the current directory, with a
/// ".restored" fallback when the name is taken. That last part is exactly what makes it
/// unpredictable to a caller — and exactly what the JSON report has to answer.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class DecodeJsonOutputPathTests
{
    [Fact]
    public void DecodeJson_NoOutputFlag_ReportsTheFallbackPathItActuallyUsed()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("payload.bin", content);
        string shards = tmp.Sub("shards");
        new ShardEncoder().Encode(input, shards,
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });

        string work = tmp.Sub("work");
        File.WriteAllBytes(Path.Combine(work, "payload.bin"), [1, 2, 3]); // name already taken

        string previous = Environment.CurrentDirectory;
        string output;
        int code;
        try
        {
            Environment.CurrentDirectory = work;
            var stdout = new StringWriter();
            code = new Cli().Run(["decode", shards, "--json"], stdout, new StringWriter());
            output = stdout.ToString();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        string path = doc.RootElement.GetProperty("restored")[0].GetProperty("outputPath").GetString()!;
        Assert.Equal("payload.restored.bin", Path.GetFileName(path));
        Assert.Equal(content, File.ReadAllBytes(path));
    }
}
