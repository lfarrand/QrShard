using System.Globalization;
using System.Text;

namespace QrShard.Tests;

/// <summary>
/// Assumptions this codebase makes about the machine it runs on, which the machine it was written
/// on happens to satisfy.
///
/// Prompted by a real one: a test compared an affordable worker count against
/// ShardDecoder.AutoParallelism, which is min(ProcessorCount, 24). On a 32-thread box the budget
/// was the binding constraint and the assertion said what it meant; on CI's 4-core runners the
/// core count bound first and it failed on all four platforms. It was asserting something about
/// the runner. These pin the properties that should hold on supported test hosts with normal
/// globalization data; invariant-globalization release binaries have separate smoke coverage.
///
/// Two of them are proven against a real defect — reverting ToLowerInvariant fails the Turkish-I
/// case, and the filesystem chain exercises code no other test reaches. The numeric-culture one is
/// a labelled canary that cannot fail today; it says so itself rather than passing for proof.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public class EnvironmentAssumptionTests
{
    private static readonly EncodeOptions Fast = new() { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 };

    // ---------- Culture ----------

    /// <summary>
    /// Cultures chosen for the two classic ways a locale breaks parsing: Turkish for the dotless-i
    /// casing rule, German for the comma decimal separator, and Arabic (Saudi) because its
    /// NumberFormatInfo differs from the invariant one in more places than most.
    /// </summary>
    public static TheoryData<string> HostileCultures => ["tr-TR", "de-DE", "ar-SA"];

    private static void InCulture(string name, Action body)
    {
        var prior = CultureInfo.CurrentCulture;
        var priorUi = CultureInfo.CurrentUICulture;
        try
        {
            var c = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = c;
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
            CultureInfo.CurrentUICulture = priorUi;
        }
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void NumericParsingDoesNotDependOnTheAmbientCulture(string culture)
    {
        // A CANARY, and worth being straight about it: this cannot fail against today's code.
        // AppSettings goes through System.Text.Json, which is invariant by construction, and
        // ParseResolution reaches int.Parse on ASCII digits, which every culture reads the same.
        // Mutating Cli's one culture-sensitive double.Parse does NOT make it fail, because that
        // call feeds only --fps, which needs ffmpeg or a camera to observe.
        //
        // It earns its place by failing the day someone routes a setting through a culture-aware
        // parse — "12.5" is 125 under de-DE, which would silently ten-fold a frame rate rather
        // than erroring. Better that it fails here than in a user's locale.
        string path = Path.Combine(Path.GetTempPath(), $"qrshard-culture-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "ReceiveFps": 12.5, "DecodeMemoryBudgetMB": 8000, "EncodeDefaults": { "Resolution": "3840x2160" } }""");
        try
        {
            var invariant = AppSettings.Load(path);
            var invariantRes = (Cli.ParseResolution("3840x2160"), Cli.ParseResolution("2160"));

            InCulture(culture, () =>
            {
                var underCulture = AppSettings.Load(path);
                Assert.Equal(invariant.ReceiveFps, underCulture.ReceiveFps);
                Assert.Equal(invariant.DecodeMemoryBudgetMB, underCulture.DecodeMemoryBudgetMB);
                Assert.Equal(invariantRes, (Cli.ParseResolution("3840x2160"), Cli.ParseResolution("2160")));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void CommandDispatchSurvivesTheTurkishI(string culture)
    {
        // Cli lowercases the verb to dispatch. Under tr-TR a culture-sensitive ToLower turns "I"
        // into a dotless "ı", so any verb containing an I stops matching — the textbook locale bug,
        // and the reason that call has to be ToLowerInvariant.
        InCulture(culture, () =>
        {
            var @out = new StringWriter();
            var err = new StringWriter();
            int exit = new Cli().Run(["INFO"], @out, err);

            // No argument was given, so it must fail — but it must fail as "info needs an image",
            // proving dispatch matched, not as "unknown command".
            Assert.NotEqual(0, exit);
            Assert.DoesNotContain("unknown", (@out.ToString() + err).ToLowerInvariant());
        });
    }

    // ---------- Filesystem ----------

    /// <summary>
    /// Names that exercise the parts of a filename most likely to be mishandled: multi-byte UTF-8,
    /// a surrogate pair (an emoji is TWO chars, so any index-based slicing can split it), and a
    /// combining accent whose enumerated spelling can vary with filesystem normalization semantics.
    /// </summary>
    public static TheoryData<string> AwkwardNames =>
    [
        "café.bin",
        "café.bin",   // same word, combining acute — NFD
        "日本語のファイル.bin",
        "target\U0001F3AF.bin", // surrogate pair
        "Ω≈ç√∫.bin",
    ];

    [Theory]
    [MemberData(nameof(AwkwardNames))]
    public void ANonAsciiFileNameSurvivesTheWholeChain(string name)
    {
        // ShardHeaderTests covers the header's own serialisation of a unicode name. Nothing covered
        // the FILESYSTEM leg: the encoder takes the name from the path, the decoder writes it back,
        // and between them sit UTF-8 byte-length arithmetic in ShardHeader.Size and the sanitising
        // in SafeFileName. A surrogate pair split by index-based slicing, or a byte count used
        // where a char count belongs, shows up here and nowhere else.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(9_000);
        string input = tmp.WriteFile(name, content);

        var encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);

        // No -o, so the decoder resolves the name itself from the header — which is the path that
        // actually exercises SafeFileName on this name.
        string previous = Environment.CurrentDirectory;
        string outDir = tmp.Sub("out");
        List<RestoredFile> restored;
        try
        {
            Environment.CurrentDirectory = outDir;
            restored = new ShardDecoder().DecodeFolder(encoded.Files, null, _ => { });
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        var only = Assert.Single(restored);
        Assert.Equal(content, File.ReadAllBytes(only.OutputPath));

        // The name has to survive too. Compare against the basename supplied to the encoder rather
        // than the literal above, because filesystems may expose a canonically equivalent spelling.
        string expected = Path.GetFileName(input);
        Assert.Equal(expected, only.FileName);

        // Re-enumerate the directory to inspect the spelling actually returned by the filesystem.
        // Checking only the header's copy above proves nothing about SafeFileName: stripping every
        // non-ASCII character would leave the header untouched and still let the content assertion
        // read back the resolved path. Verified by mutation — that exact change is caught here.
        string onDisk = Assert.Single(Directory.EnumerateFiles(outDir));
        Assert.Equal(expected.Normalize(NormalizationForm.FormC),
                     Path.GetFileName(onDisk).Normalize(NormalizationForm.FormC));
    }

    // ---------- Core count ----------

    [Fact]
    public void DecodingWorksWithASingleWorker()
    {
        // Everything about the decode pool — the work-stealing cursor, the per-worker scratch, the
        // budget arithmetic — is written for a machine with cores to spare, and every test runs on
        // one. A 1-2 core VM gets the degenerate case, where the cursor has no one to steal from.
        using var tmp = new TempDir();
        string settings = tmp.File("appsettings.json");
        File.WriteAllText(settings, """{ "DecodeMaxParallelism": 1 }""");
        var single = AppSettings.Load(settings);
        Assert.Equal(1, single.DecodeMaxParallelism);

        byte[] content = TestData.Random(120_000); // spans several images
        string input = tmp.WriteFile("input.bin", content);
        var encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        Assert.True(encoded.Files.Count > 1, "need a multi-image set for the worker pool to matter");

        var decoder = new ShardDecoder(
            single, new CameraRectifier(), new FrameLocator(new InnerRectScanner(), new StripReader()),
            new StripReader(), new GridSampler(), new ShardAssembler(),
            new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        string restored = tmp.File("out.bin");
        decoder.DecodeFolder(encoded.Files, restored, _ => { });
        Assert.Equal(content, File.ReadAllBytes(restored));
    }
}
