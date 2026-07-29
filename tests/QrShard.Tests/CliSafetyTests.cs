using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The CLI surface, where the failure mode that matters is doing something plausible-looking and
/// wrong without saying so. All three of these were silent: a password nobody meant, an option
/// accepted and discarded, and a hostile string handed straight to the terminal.
/// </summary>
public class CliSafetyTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli().Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void PasswordThatSwallowedTheNextOption_IsRejectedRatherThanUsed()
    {
        // `-p --json` parsed as Password = "--json" and encoded, exit 0. The user believed they had
        // supplied their own password; they can never decrypt the result, and nothing in the output
        // said why. -p was exempt from the missing-value guard because a password may start with
        // '-' — true, but the guard only fires when the value is EXACTLY one of this command's
        // options, which no real password is.
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(1000));

        var (code, _, err) = Run("encode", input, "-o", tmp.File("s"), "-r", "900", "-p", "--json");

        Assert.Equal(2, code);
        Assert.Contains("--json", err);
        Assert.Contains("password", err);
        Assert.False(Directory.Exists(tmp.File("s"))); // nothing encoded under the wrong key
    }

    [Fact]
    public void PasswordStartingWithADash_IsStillAccepted()
    {
        // The narrowing must not cost the legitimate case the exemption existed for.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(2000);
        string input = tmp.WriteFile("in.bin", content);
        string shards = tmp.File("s");

        var (code, _, err) = Run("encode", input, "-o", shards, "-r", "900", "-p", "-not-an-option");
        Assert.Equal(0, code);
        Assert.True(string.IsNullOrWhiteSpace(err), err);

        // And it round-trips under that password, so it really was used as one.
        string restored = tmp.File("out.bin");
        var (dcode, _, _) = Run("decode", shards, "-o", restored, "-p", "-not-an-option");
        Assert.Equal(0, dcode);
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void SessionOnARecording_SaysItDoesNotApplyInsteadOfIgnoringIt()
    {
        // --session is in decode's allowlist and the help offered it as a general decode option,
        // but the recording branch returns before sessionPath is read: parsed, validated, and
        // thrown away. Someone decoding a recording of an incomplete transfer got no partial
        // progress and no hint that the flag they passed to preserve it did nothing.
        using var tmp = new TempDir();
        // Any file the video branch claims is enough; the option is rejected before decoding.
        string fake = tmp.File("recording.mp4");
        File.WriteAllBytes(fake, TestData.Random(64));

        var (code, _, err) = Run("decode", fake, "--session", tmp.File("s.qrsession"), "-o", tmp.File("o.bin"));

        Assert.Equal(2, code);
        Assert.Contains("--session", err);
    }

    [Theory]
    // A terminal is an interpreter, and this field is attacker-controlled: Deserialize takes up to
    // 4096 bytes of arbitrary UTF-8 with only a CRC the crafter also computes. Escape sequences
    // could rewrite earlier output — forging a "SHA-256 verified" line, or hiding an entry from a
    // `verify` listing. A bare CR is enough to overwrite the line just printed.
    [InlineData("plain.bin", "plain.bin")]
    [InlineData("a\rb.bin", "a?b.bin")]
    [InlineData("a\nb.bin", "a?b.bin")]
    [InlineData("[2J[Hwiped.bin", "?[2J?[Hwiped.bin")]
    [InlineData("tab\there.bin", "tab?here.bin")]
    [InlineData("del.bin", "del?.bin")]
    [InlineData("c1[31m.bin", "c1?[31m.bin")]
    // Legitimate non-ASCII must survive — plenty of real file names are not Latin.
    [InlineData("café-Ω-日本.bin", "café-Ω-日本.bin")]
    public void Display_NeutralisesControlCharactersButKeepsRealText(string raw, string expected) =>
        Assert.Equal(expected, ShardHeader.Display(raw));

    [Fact]
    public void Display_CapsRunawayNames()
    {
        // 4 KB of name would flood a progress listing on its own, whatever it contained.
        string huge = new('x', 4096);
        string shown = ShardHeader.Display(huge);
        Assert.True(shown.Length < 200, $"still {shown.Length} chars");
        Assert.EndsWith("...", shown);
    }
}
