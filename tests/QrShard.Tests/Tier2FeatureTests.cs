using System.Security.Cryptography;
using QrShard;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Tier-2 round: header-AAD authentication, capture-quality heatmap, Sauvola
/// binarization, and temporal frame averaging.</summary>
public class Tier2FeatureTests
{
    // ---- Header-AAD: bind the identity header as AES-GCM associated data ----

    [Fact]
    public void Cipher_TamperedAad_FailsDecryption()
    {
        var cipher = new PayloadCipher();
        byte[] plain = TestData.Random(2000);
        byte[] sha = SHA256.HashData(plain);
        var aad = PayloadCipher.BuildAad(plain.Length, sha, "real.bin");
        byte[] blob = cipher.Encrypt(plain, "pw", aad);

        Assert.Equal(plain, cipher.Decrypt(blob, "pw", "real.bin", aad));            // correct AAD → ok
        var tamperedName = PayloadCipher.BuildAad(plain.Length, sha, "evil.bin");    // filename changed
        Assert.Throws<ShardDecodeException>(() => cipher.Decrypt(blob, "pw", "evil.bin", tamperedName));
        var tamperedLen = PayloadCipher.BuildAad(plain.Length + 1, sha, "real.bin"); // length changed
        Assert.Throws<ShardDecodeException>(() => cipher.Decrypt(blob, "pw", "real.bin", tamperedLen));
    }

    [Fact]
    public void EncryptedEncode_SetsAuthMetaFlag_AndRoundTrips()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(20_000);
        string input = tmp.WriteFile("secret.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, Password = "hunter2" });

        var header = new ShardDecoder().Diagnose(result.Files[0]).Shard!.Header;
        Assert.True((header.Flags & ShardHeader.FlagEncrypted) != 0);
        Assert.True((header.Flags & ShardHeader.FlagAuthMeta) != 0);

        string output = tmp.File("out.bin");
        new ShardDecoder().DecodeFolder(result.Files, output, _ => { }, "hunter2");
        Assert.Equal(content, File.ReadAllBytes(output));
    }

    [Fact]
    public void EncryptedAssembly_TamperedHeaderFilename_FailsDecryption()
    {
        // The point of AAD: an attacker can recompute the header CRC (it is not a MAC) but not the
        // GCM tag. Decode reconstructs the AAD from the (tampered) header, so a changed filename
        // makes decryption fail rather than silently mis-routing a write.
        using var tmp = new TempDir();
        byte[] content = TestData.Random(6000);
        string input = tmp.WriteFile("payroll.bin", content);
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, Password = "pw" });

        var shards = new ShardDecoder().CollectShards(result.Files, _ => { });
        // Correct headers assemble fine.
        Assert.Equal(content, AssembleToBytes(shards, tmp, "pw"));

        // Tamper the filename on every shard (with an internally-consistent header) and reassemble.
        var tampered = shards
            .Select(s => new DecodedShard(CloneHeaderWithName(s.Header, "innocent.bin"), s.Payload,
                s.SourceFile, s.EccParity, s.CorrectedBytes))
            .ToList();
        var ex = Assert.Throws<ShardDecodeException>(
            () => new ShardAssembler().Assemble(tampered, tmp.File("out2.bin"), _ => { }, "pw"));
        Assert.Contains("tampered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Capture-quality heatmap (works on failed captures) ----

    [Fact]
    public void QualityHeatmap_CapturesPerCellMargins_AndRenders()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("in.bin", TestData.Random(8000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, EccParity = 16 });

        var diag = new ShardDecoder().Diagnose(result.Files[0]);
        Assert.NotNull(diag.Layout);
        Assert.NotNull(diag.CellMargins); // captured even for diagnostics, decode success or not
        Assert.Equal(diag.Layout.GridW * diag.Layout.GridH, diag.CellMargins.Length);

        string heat = tmp.File("quality.png");
        new HeatmapRenderer().RenderQuality(diag.Layout, diag.CellMargins, heat);
        Assert.True(File.Exists(heat));
    }

    // ---- Sauvola binarization ----

    [Fact]
    public void Sauvola_FlatBrightField_HasNoSpuriousDark_ThinDarkLineDetected()
    {
        const int n = 160;
        var flat = new Rgb24[n * n];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = new Rgb24(235, 235, 235);

        // The local-contrast term means a perfectly flat field yields ZERO dark cells — the
        // spurious-run suppression that is Sauvola's whole point over a plain mean-ratio.
        var darkFlat = new AdaptiveBinarizer().Threshold(new Bitmap((Rgb24[])flat.Clone(), n, n));
        Assert.Equal(0, darkFlat.Count(d => d));

        // A thin dark line against the bright field IS foreground (local contrast present).
        var lined = (Rgb24[])flat.Clone();
        for (int y = 0; y < n; y++)
        {
            lined[y * n + 79] = new Rgb24(20, 20, 20);
            lined[y * n + 80] = new Rgb24(20, 20, 20);
        }
        var darkLine = new AdaptiveBinarizer().Threshold(new Bitmap(lined, n, n));
        Assert.True(darkLine[80 * n + 79]);  // on the line → dark
        Assert.False(darkLine[80 * n + 10]); // bright, away from the line → not dark
    }

    // ---- Temporal frame averaging ----

    [Fact]
    public void TemporalAveraging_MeanReconstructsCleanFrame()
    {
        const int n = 8;
        var clean = new Rgb24[n * n];
        var rngC = new Random(1);
        for (int i = 0; i < clean.Length; i++)
            clean[i] = new Rgb24((byte)rngC.Next(256), (byte)rngC.Next(256), (byte)rngC.Next(256));

        int frames = 40;
        var sum = new int[n * n * 3];
        var noise = new Random(2);
        for (int f = 0; f < frames; f++)
        {
            var px = new Rgb24[n * n];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Rgb24(Jitter(clean[i].R, noise), Jitter(clean[i].G, noise), Jitter(clean[i].B, noise));
            VideoDecoder.Accumulate(sum, new Bitmap(px, n, n));
        }
        var avg = VideoDecoder.BuildAverage(sum, n, n, frames);
        for (int i = 0; i < clean.Length; i++)
        {
            Assert.True(Math.Abs(avg.Px[i].R - clean[i].R) <= 6);
            Assert.True(Math.Abs(avg.Px[i].G - clean[i].G) <= 6);
        }
    }

    // Note: no end-to-end "averaging rescues a video" test. Averaging only accumulates
    // near-duplicate frames (below the dedup threshold), and for sharp cells those are
    // classification-identical (palette spacing ≥ 36 ≫ the <3 inter-frame delta) — it helps only
    // for blurred cells near palette boundaries, a regime whose precisely-marginal base is not
    // reproducible deterministically. The averaging is failure-path only and CRC-gated, so it
    // cannot regress; the mechanism is covered above and the full video suite guards the path.

    // ---- helpers ----

    private static byte Jitter(byte v, Random rng) => (byte)Math.Clamp(v + rng.Next(-15, 16), 0, 255);

    private static byte[] AssembleToBytes(List<DecodedShard> shards, TempDir tmp, string password)
    {
        string outPath = tmp.File("assembled-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        new ShardAssembler().Assemble(shards, outPath, _ => { }, password);
        return File.ReadAllBytes(outPath);
    }

    private static ShardHeader CloneHeaderWithName(ShardHeader h, string name) => new()
    {
        FileId = h.FileId, Index = h.Index, Count = h.Count, PayloadLength = h.PayloadLength,
        PayloadCrc32 = h.PayloadCrc32, TotalLength = h.TotalLength, OriginalLength = h.OriginalLength,
        Flags = h.Flags, Sha256 = h.Sha256, FileName = name, StripeData = h.StripeData, StripeParity = h.StripeParity,
    };
}
