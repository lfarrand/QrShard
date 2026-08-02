using QrShard;

namespace QrShard.Tests;

/// <summary>
/// The CLI surface, where the failure mode that matters is doing something plausible-looking and
/// wrong without saying so. All three of these were silent: a password nobody meant, an option
/// accepted and discarded, and a hostile string handed straight to the terminal.
/// </summary>
public class CliSafetyTests
{
    public static TheoryData<string[]> VideoOnlyEncodeOptions => new()
    {
        new[] { "--open" },
        new[] { "--slideshow", "html" },
        new[] { "--interval", "500" },
    };

    public static TheoryData<string[], string> InvalidReceiveCombinations => new()
    {
        { new[] { "receive", "ignored" }, "positional" },
        { new[] { "receive", "--screen", "--device", "camera" }, "--device" },
        { new[] { "receive", "--screen", "--format", "dshow" }, "--format" },
        { new[] { "receive", "--region", "0,0,100,100" }, "--region" },
    };

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli().Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static (int Code, string Out, string Err) RunWithInput(string input, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli().Run(args, stdout, stderr, new StringReader(input));
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
    [MemberData(nameof(VideoOnlyEncodeOptions))]
    public void SlideshowOnlyOptionsRequireVideoInsteadOfBeingIgnored(string[] options)
    {
        var (code, _, err) = Run(["encode", .. options]);

        Assert.Equal(2, code);
        Assert.Contains("require --video", err);
    }

    [Fact]
    public void SlideshowKindMustBeExactlyHtmlOrApng()
    {
        var (code, _, err) = Run("encode", "--video", "--slideshow", "gif");

        Assert.Equal(2, code);
        Assert.Contains("html", err);
        Assert.Contains("apng", err);
    }

    [Fact]
    public void ClipboardRejectsPositionalInputsInsteadOfIgnoringThem()
    {
        var (code, _, err) = Run("decode", "ignored.png", "--clipboard");

        Assert.Equal(2, code);
        Assert.Contains("does not accept", err);
        Assert.Contains("arguments", err);
    }

    [Theory]
    [MemberData(nameof(InvalidReceiveCombinations))]
    public void ReceiveRejectsArgumentsAndCombinationsItWouldIgnore(string[] args, string expected)
    {
        var (code, _, err) = Run(args);

        Assert.Equal(2, code);
        Assert.Contains(expected, err);
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
    [InlineData("reverse\u202egpj.exe", "reverse?gpj.exe")]
    [InlineData("isolate\u2066name\u2069.bin", "isolate?name?.bin")]
    [InlineData("lines\u2028paragraph\u2029.bin", "lines?paragraph?.bin")]
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

    [Fact]
    public void CliErrorsNeutraliseControlCharactersFromArguments()
    {
        var (code, _, err) = Run("encode", "missing\u001b[2J\rforged.bin");

        Assert.Equal(2, code);
        Assert.DoesNotContain('\u001b', err);
        Assert.DoesNotContain("\rforged", err);
        Assert.DoesNotContain("\nforged", err);
        Assert.Contains("missing?[2J?forged.bin", err);
    }

    [Fact]
    public void FileSelfTestRoundTripsEncryptedInput()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("encrypted.bin", TestData.Random(128));

        var (code, output, err) = Run("test", input, "-r", "700", "-c", "8", "-p", "secret");

        Assert.Equal(0, code);
        Assert.Contains("PASS", output);
        Assert.True(string.IsNullOrWhiteSpace(err), err);
    }

    [Fact]
    public void FileSelfTestSupportsNonPngEncodeFormats()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("format.bin", TestData.Random(128));

        var (code, output, err) = Run("test", input, "-r", "700", "-c", "8", "-f", "bmp");

        Assert.Equal(0, code);
        Assert.Contains("PASS", output);
        Assert.True(string.IsNullOrWhiteSpace(err), err);
    }

    [Fact]
    public void EmptyPasswordIsRejectedBeforeOutputPublication()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(32));
        string output = tmp.File("shards");

        var (code, _, err) = Run("encode", input, "-o", output, "-r", "700", "-p", "");

        Assert.NotEqual(0, code);
        Assert.Contains("must not be empty", err);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PasswordStdinAndFileAvoidArgvAndRoundTrip()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(128);
        string source = tmp.WriteFile("secret.bin", content);
        string shards = tmp.File("shards");

        var encoded = RunWithInput("correct horse\n", "encode", source, "-o", shards,
            "-r", "700", "-c", "8", "--password-stdin");
        Assert.Equal(0, encoded.Code);

        byte[] passwordBytes = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes("correct horse\r\n")).ToArray();
        string passwordFile = tmp.WriteFile("password.txt", passwordBytes);
        string restored = tmp.File("restored.bin");
        var decoded = Run("decode", shards, "-o", restored, "--password-file", passwordFile);

        Assert.Equal(0, decoded.Code);
        Assert.Equal(content, File.ReadAllBytes(restored));
    }

    [Fact]
    public void ExactPasswordCharacterLimitAllowsStdinAndFileFraming()
    {
        using var tmp = new TempDir();
        string source = tmp.WriteFile("secret.bin", TestData.Random(32));
        string password = new('p', 4_096);

        var fromStdin = RunWithInput(password + "\r\n", "encode", source,
            "-o", tmp.File("stdin-shards"), "-r", "700", "-c", "8", "--password-stdin");
        Assert.Equal(0, fromStdin.Code);

        byte[] framedFile = System.Text.Encoding.UTF8.GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(password + "\r\n")).ToArray();
        string passwordFile = tmp.WriteFile("password-limit.txt", framedFile);
        var fromFile = Run("encode", source, "-o", tmp.File("file-shards"),
            "-r", "700", "-c", "8", "--password-file", passwordFile);
        Assert.Equal(0, fromFile.Code);
    }

    [Fact]
    public void PasswordFramingDoesNotHideAnOverLimitLogicalPassword()
    {
        using var tmp = new TempDir();
        string source = tmp.WriteFile("secret.bin", TestData.Random(32));
        string output = tmp.File("shards");

        var result = RunWithInput(new string('p', 4_097) + "\r\n", "encode", source,
            "-o", output, "-r", "700", "-c", "8", "--password-stdin");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("at most 4,096 characters", result.Err);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PasswordFileRejectsUtf16RatherThanSilentlyAutodetectingIt()
    {
        using var tmp = new TempDir();
        string source = tmp.WriteFile("secret.bin", TestData.Random(32));
        byte[] utf16 = System.Text.Encoding.Unicode.GetPreamble()
            .Concat(System.Text.Encoding.Unicode.GetBytes("secret")).ToArray();
        string passwordFile = tmp.WriteFile("password-utf16.txt", utf16);
        string output = tmp.File("shards");

        var (code, _, _) = Run("encode", source, "-o", output, "--password-file", passwordFile);

        Assert.NotEqual(0, code);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PasswordSourcesAreMutuallyExclusive()
    {
        using var tmp = new TempDir();
        string source = tmp.WriteFile("in.bin", [1]);

        var (code, _, err) = RunWithInput("other\n", "encode", source, "-p", "one", "--password-stdin");

        Assert.NotEqual(0, code);
        Assert.Contains("exactly one password source", err);
    }

    [Theory]
    [InlineData("-o", "first", "--out", "second")]
    [InlineData("--password", "first", "-p", "second")]
    [InlineData("--json", "", "--json", "")]
    public void DuplicateOptionsAndAliasesAreRejected(string first, string firstValue,
        string second, string secondValue)
    {
        var args = new List<string> { "encode" };
        args.Add(first);
        if (firstValue.Length > 0) args.Add(firstValue);
        args.Add(second);
        if (secondValue.Length > 0) args.Add(secondValue);

        var (code, _, err) = Run([.. args]);

        Assert.Equal(2, code);
        Assert.Contains("specify", err);
    }

    [Fact]
    public void DecodeFpsIsRejectedForNonRecordingInput()
    {
        var (code, _, err) = Run("decode", "missing.png", "--fps", "NaN");

        Assert.Equal(2, code);
        Assert.Contains("recording", err);
    }

    [Theory]
    [InlineData("--camera")]
    [InlineData("-o", "ignored")]
    [InlineData("--resolution", "700")]
    public void CalibrationAnalysisRejectsGenerationOnlyOptions(params string[] option)
    {
        using var tmp = new TempDir();
        string captures = tmp.Sub("captures");

        var (code, _, err) = Run(["calibrate", captures, .. option]);

        Assert.Equal(2, code);
        Assert.Contains("cannot be used", err);
    }

    [Fact]
    public void BuiltInSelfTestRejectsFileSpecificSettings()
    {
        var (code, _, err) = Run("test", "--profile", "does-not-exist");

        Assert.Equal(2, code);
        Assert.Contains("built-in self-test takes no options", err);
    }

    [Fact]
    public void WindowsExtendedPathCanonicalizesToTheDosPath()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string normal = tmp.File("same.bin");
        string extended = @"\\?\" + normal;

        Assert.Equal(Cli.CanonicalPath(normal), Cli.CanonicalPath(extended),
            StringComparer.OrdinalIgnoreCase);
    }
}
