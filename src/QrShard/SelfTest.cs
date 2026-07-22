using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QrShard;

/// <summary>
/// End-to-end round-trip tests at real resolutions, including simulated screenshot captures
/// (padding around the code, fractional up/down scaling, cursor damage).
/// </summary>
[ExcludeFromCodeCoverage] // exercised via `qrshard test`; unit tests cover the codec itself
internal sealed class SelfTest(IShardEncoder encoder, IShardDecoder decoder) : ISelfTest
{
    public bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "qrshard-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        bool allPass = true;
        try
        {
            allPass &= Case(root, "small text, defaults", MakeText(20_000), new EncodeOptions(), [1.0, 1.25, 1.5]);
            allPass &= Case(root, "1 MB random, defaults + cursor damage", MakeRandom(1_000_000), new EncodeOptions(), [1.0, 1.25, 1.5], damage: true);
            allPass &= Case(root, "random, dense (cell 2, 6 bits)", MakeRandom(2_000_000),
                new EncodeOptions { CellPx = 2, BitsPerCell = 6 }, [1.0]);
            allPass &= Case(root, "random, max density (cell 1, 8 bits, 4096px)", MakeRandom(4_000_000),
                new EncodeOptions { Width = 4096, Height = 4096, CellPx = 1, BitsPerCell = 8 }, []);
            allPass &= Case(root, "random, 4K widescreen (3840x2160, cell 1, 6 bits)", MakeRandom(3_000_000),
                new EncodeOptions { Width = 3840, Height = 2160, CellPx = 1, BitsPerCell = 6 }, []);
            allPass &= Case(root, "empty file", [], new EncodeOptions(), [1.0, 1.25, 1.5]);
            allPass &= RecoveryCase(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        Console.WriteLine(allPass ? "\nAll self-tests passed." : "\nSELF-TEST FAILURES — see above.");
        return allPass;
    }

    /// <summary>
    /// Round-trips the USER's own file at THEIR settings through the simulated-screenshot
    /// degradation and reports the ECC headroom actually consumed. Answers "will my file at my
    /// settings survive?" — which the fixed-fixture <see cref="Run"/> cannot. It is a FLOOR
    /// (clean synthetic pad + rescale + cursor), not a real-capture guarantee.
    /// </summary>
    public int RunFile(string filePath, EncodeOptions opt, TextWriter output)
    {
        string root = Path.Combine(Path.GetTempPath(), "qrshard-filetest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            byte[] wantSha;
            using (var fs = File.OpenRead(filePath))
                wantSha = SHA256.HashData(fs);
            long len = new FileInfo(filePath).Length;

            string shardDir = Path.Combine(root, "shards");
            var result = encoder.Encode(filePath, shardDir, opt);
            output.WriteLine($"Testing '{Path.GetFileName(filePath)}' ({len:N0} bytes) at your settings:");
            output.WriteLine($"  {result.ImageCount} image(s), {result.Width}x{result.Height}px, cell {opt.CellPx}px, " +
                             $"{opt.BitsPerCell} bits/cell, ECC {opt.EccParity}{(opt.CameraMode ? ", camera profile" : "")}.");

            bool exact = DecodeMatches(shardDir, wantSha, out _);
            output.WriteLine($"  {(exact ? "PASS" : "FAIL")}  exact round-trip (pristine images)");

            bool degradedOk = true;
            double worstUtil = -1;
            foreach (double scale in new[] { 1.0, 1.25, 1.5 })
            {
                string capDir = Path.Combine(root, $"cap{(int)(scale * 100)}");
                Directory.CreateDirectory(capDir);
                bool damage = scale == 1.25; // one pass draws a cursor over the data area
                foreach (string f in result.Files)
                    SimulateScreenshot(f, Path.Combine(capDir, Path.GetFileName(f)), scale, damage);
                bool ok = DecodeMatches(capDir, wantSha, out double util);
                degradedOk &= ok;
                if (util >= 0)
                    worstUtil = Math.Max(worstUtil, util);
                string utilNote = util >= 0 ? $", ECC used {util:P0}" : "";
                output.WriteLine($"  {(ok ? "PASS" : "FAIL")}  simulated screenshot: pad + {scale:0.00}x scale" +
                                 $"{(damage ? " + cursor damage" : "")}{utilNote}");
            }

            bool pass = exact && degradedOk;
            output.WriteLine();
            if (pass)
            {
                string margin = worstUtil < 0 ? "no ECC — add --ecc for headroom against worse captures"
                    : worstUtil < 0.5 ? "comfortable headroom"
                    : worstUtil < 0.8 ? "tight — consider more --ecc or lower density"
                    : "very tight — raise --ecc or lower density";
                output.WriteLine(worstUtil >= 0
                    ? $"PASS — survives the simulated-screenshot floor (worst-case ECC used {worstUtil:P0}, {margin})."
                    : $"PASS — survives the simulated-screenshot floor ({margin}).");
                output.WriteLine("This is a FLOOR: synthetic padding + rescale (+ a cursor). Real photos, glare, and angle");
                output.WriteLine("are harsher — run `qrshard calibrate` against your actual screen/camera for real-world settings.");
            }
            else
            {
                output.WriteLine("FAIL — your file did NOT survive the simulated degradation at these settings.");
                output.WriteLine("Lower the density (bigger --cell, fewer --bits), raise --ecc, or add --recovery, then test again.");
            }
            return pass ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or ShardDecodeException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            output.WriteLine($"error: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Decodes an image folder and checks the SHA against the original; also reports the
    /// worst-case ECC utilization across the images (-1 when no ECC or nothing decoded).</summary>
    private bool DecodeMatches(string imageDir, byte[] wantSha, out double worstUtilization)
    {
        worstUtilization = -1;
        var images = Directory.EnumerateFiles(imageDir, "*.png").ToList();
        string outPath = Path.Combine(imageDir, "restored.out");
        try
        {
            decoder.DecodeFolder(images, outPath, _ => { });
        }
        catch (ShardDecodeException)
        {
            return false;
        }

        foreach (string img in images)
        {
            var diag = decoder.Diagnose(img);
            if (diag.Layout is { EccParity: > 0 } layout && diag.Shard is not null)
            {
                double correctable = Math.Max(1, layout.CodewordCount * (layout.EccParity / 2.0));
                worstUtilization = Math.Max(worstUtilization, diag.Shard.CorrectedBytes / correctable);
            }
        }

        byte[] gotSha;
        using (var fs = File.OpenRead(outPath))
            gotSha = SHA256.HashData(fs);
        return gotSha.AsSpan().SequenceEqual(wantSha);
    }

    private bool Case(string root, string name, byte[] content, EncodeOptions opt, double[] captureScales, bool damage = false)
    {
        Console.WriteLine($"\n=== {name} ({content.Length:N0} bytes) ===");
        string dir = Path.Combine(root, name.GetHashCode().ToString("x8"));
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "testfile.bin");
        File.WriteAllBytes(input, content);
        byte[] wantSha = SHA256.HashData(content);

        bool pass = true;
        try
        {
            string shardDir = Path.Combine(dir, "shards");
            var result = encoder.Encode(input, shardDir, opt);
            Console.WriteLine($"  encoded: {result.ImageCount} image(s), {result.Width}x{result.Height}, capacity {result.BytesPerImage:N0} B/image");

            pass &= Verify("exact round-trip", shardDir, dir, wantSha);

            foreach (double scale in captureScales)
            {
                string capDir = Path.Combine(dir, $"cap{(int)(scale * 100)}");
                Directory.CreateDirectory(capDir);
                foreach (string f in result.Files)
                    SimulateScreenshot(f, Path.Combine(capDir, Path.GetFileName(f)), scale, damage);
                string label = $"simulated screenshot, pad + {scale:0.00}x scale{(damage ? " + cursor damage" : "")}";
                pass &= Verify(label, capDir, dir, wantSha);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.Message}");
            pass = false;
        }
        return pass;
    }

    /// <summary>Encodes with cross-shard parity, deletes whole images, and verifies reconstruction.</summary>
    private bool RecoveryCase(string root)
    {
        Console.WriteLine("\n=== cross-shard recovery (1.5 MB, 25% parity) ===");
        string dir = Path.Combine(root, "recovery");
        Directory.CreateDirectory(dir);
        byte[] content = MakeRandom(1_500_000);
        string input = Path.Combine(dir, "testfile.bin");
        File.WriteAllBytes(input, content);
        byte[] wantSha = SHA256.HashData(content);

        try
        {
            string shardDir = Path.Combine(dir, "shards");
            var result = encoder.Encode(input, shardDir, new EncodeOptions { RecoveryPercent = 25 });
            Console.WriteLine($"  encoded: {result.DataImages} data + {result.ParityImages} parity, " +
                              $"tolerates {result.StripeParity} loss per {result.StripeData + result.StripeParity}");

            // Delete the per-stripe parity budget worth of data images.
            var dataImages = result.Files.Where(f => !f.Contains("parity")).ToList();
            foreach (string f in dataImages.Take(result.StripeParity))
                File.Delete(f);
            Console.WriteLine($"  deleted {result.StripeParity} whole data image(s)");

            string outPath = Path.Combine(dir, "restored.bin");
            decoder.DecodeFolder(Directory.EnumerateFiles(shardDir, "*.png"), outPath, _ => { });
            bool ok = SHA256.HashData(File.ReadAllBytes(outPath)).AsSpan().SequenceEqual(wantSha);
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: rebuilt from parity after image loss");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.Message}");
            return false;
        }
    }

    private bool Verify(string label, string imageDir, string workDir, byte[] wantSha)
    {
        var messages = new List<string>();
        try
        {
            string outPath = Path.Combine(workDir, $"restored-{Guid.NewGuid().ToString("N")[..8]}.bin");
            decoder.DecodeFolder(Directory.EnumerateFiles(imageDir, "*.png"), outPath, messages.Add);
            byte[] gotSha = SHA256.HashData(File.ReadAllBytes(outPath));
            bool ok = gotSha.AsSpan().SequenceEqual(wantSha);
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {label}");
            return ok;
        }
        catch (ShardDecodeException ex)
        {
            Console.WriteLine($"  FAIL: {label}: {ex.Message}");
            foreach (string m in messages.Where(m => m.Contains("FAILED")))
                Console.WriteLine($"    {m.Trim()}");
            return false;
        }
    }

    /// <summary>
    /// Pads the shard onto a larger desktop-like canvas at an offset, optionally draws a fake
    /// mouse cursor over the data area, then rescales the whole capture.
    /// </summary>
    private static void SimulateScreenshot(string srcPath, string dstPath, double scale, bool damage)
    {
        using var src = Image.Load<Rgb24>(srcPath);
        if (damage)
            DrawCursor(src, src.Width / 2 + 100, src.Height / 2 - 60);
        int padL = 83, padT = 47, padR = 131, padB = 202;
        using var canvas = new Image<Rgb24>(src.Width + padL + padR, src.Height + padT + padB, new Rgb24(226, 229, 233));
        canvas.Mutate(c =>
        {
            c.DrawImage(src, new Point(padL, padT), 1f);
            if (scale != 1.0)
                c.Resize((int)(canvas.Width * scale), 0, KnownResamplers.Bicubic);
        });
        canvas.SaveAsPng(dstPath);
    }

    /// <summary>Draws a rough white arrow-cursor shape with a black outline, ~20x30 px.</summary>
    private static void DrawCursor(Image<Rgb24> img, int cx, int cy)
    {
        img.ProcessPixelRows(acc =>
        {
            for (int dy = 0; dy < 30; dy++)
            {
                int width = Math.Min(dy * 2 / 3 + 1, 20);
                var row = acc.GetRowSpan(Math.Clamp(cy + dy, 0, img.Height - 1));
                for (int dx = 0; dx < width; dx++)
                {
                    int x = Math.Clamp(cx + dx, 0, img.Width - 1);
                    bool outline = dx == 0 || dx == width - 1 || dy == 29;
                    row[x] = outline ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255);
                }
            }
        });
    }

    private static byte[] MakeText(int length)
    {
        var line = "The quick brown fox jumps over the lazy dog. 0123456789.\n"u8.ToArray();
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = line[i % line.Length];
        return data;
    }

    private static byte[] MakeRandom(int length)
    {
        var data = new byte[length];
        new Random(12345).NextBytes(data);
        return data;
    }
}
