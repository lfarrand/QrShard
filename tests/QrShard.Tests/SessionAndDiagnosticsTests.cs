using QrShard;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    public void SessionSave_TightensExistingUnixModeToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string session = tmp.WriteFile("permissive.qrsession", "old"u8.ToArray());
        File.SetUnixFileMode(session, ShardAssembler.PortableUnixFileModeMask |
            UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit);

        new SessionStore().Save(session, Array.Empty<DecodedShard>());

        UnixFileMode mode = File.GetUnixFileMode(session);
        Assert.Equal((UnixFileMode)0, mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SessionSave_TightensExistingWindowsDaclToCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var tmp = new TempDir();
        string session = tmp.WriteFile("permissive.qrsession", "old"u8.ToArray());
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier current = identity.User!;
        var permissive = new FileSecurity();
        permissive.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        permissive.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            AccessControlType.Allow));
        permissive.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(session).SetAccessControl(permissive);

        new SessionStore().Save(session, Array.Empty<DecodedShard>());

        FileSecurity acl = new FileInfo(session)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(current, acl.GetOwner(typeof(SecurityIdentifier)));
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(current, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        });
    }

    [Fact]
    public void SessionLoad_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(new SessionStore().Load(Path.Combine(Path.GetTempPath(), "does-not-exist.qrsession")));
    }

    [Fact]
    public void SessionLoad_DoesNotAllocateADeclaredPayloadThatIsNotInTheFile()
    {
        using var tmp = new TempDir();
        string path = tmp.File("short.qrsession");
        using (var fs = File.Create(path))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("QRSS"u8);
            writer.Write((byte)1);
            writer.Write(1);           // entry count
            writer.Write(0);           // header length
            writer.Write(32_000_000);  // absent payload: old loader allocated this before checking EOF
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(new SessionStore().Load(path));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"malformed session allocated {allocated:N0} bytes");
    }

    [Fact]
    public void SessionLoad_RejectsHeadersWithUnknownFlags()
    {
        using var tmp = new TempDir();
        var header = new ShardHeader
        {
            FileId = 3,
            Index = 0,
            Count = 1,
            PayloadLength = 0,
            PayloadCrc32 = 0,
            TotalLength = 0,
            OriginalLength = 0,
            Flags = 0x80,
            Sha256 = SHA256.HashData([]),
            FileName = "future.bin",
        };
        byte[] headerBytes = header.Serialize();
        string path = tmp.File("future.qrsession");
        using (var fs = File.Create(path))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("QRSS"u8);
            writer.Write((byte)1);
            writer.Write(1);
            writer.Write(headerBytes.Length);
            writer.Write(headerBytes);
            writer.Write(0);
        }

        Assert.Empty(new SessionStore().Load(path));
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
        Assert.Equal(3, codeIncomplete);
        Assert.Contains("missing image(s) 2", outIncomplete);
    }

    [Fact]
    public void Verify_SeparatesIncompleteFromUnusable()
    {
        // The two outcomes need different codes because they need different reactions: capturing
        // more images fixes the first and can never fix the second. Collapsing both into 1 (as
        // this did) left a script no way to tell "keep going" from "give up".
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(150_000));
        var result = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        File.Delete(result.Files[1]);
        Assert.Equal(3, Run("verify", tmp.File("shards")).Code);

        string junk = tmp.Sub("junk");
        File.WriteAllBytes(Path.Combine(junk, "noise.png"), TestData.Random(2_000));
        Assert.Equal(1, Run("verify", junk).Code);
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
