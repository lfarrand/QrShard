using System.Buffers.Binary;
using System.Security.Cryptography;
using QrShard;

namespace QrShard.Tests;

/// <summary>Destructive-path, corruption, migration, budget, and lease tests for sessions.</summary>
public sealed class SessionSafetyTests
{
    private static readonly EncodeOptions Fast = new()
    {
        Width = 900,
        Height = 900,
        CellPx = 3,
        BitsPerCell = 4,
    };

    private static List<DecodedShard> Shards(TempDir tmp, int bytes = 150_000)
    {
        string input = tmp.WriteFile("source.bin", TestData.Random(bytes));
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("encoded"), Fast);
        return new ShardDecoder().CollectShards(encoded.Files, _ => { });
    }

    private static (int Code, string Out, string Error) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = new Cli(new AppSettings()).Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ForeignAndUnsupportedFilesFailWithoutChangingOneByte()
    {
        using var tmp = new TempDir();
        var store = new SessionStore();
        foreach ((string name, byte[] bytes) in new[]
        {
            ("foreign.bin", TestData.Random(32_000)),
            ("future.qrsession", new byte[] { (byte)'Q', (byte)'R', (byte)'S', (byte)'S', 99, 1, 2, 3, 4 }),
        })
        {
            string path = tmp.WriteFile(name, bytes);
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => store.Load(path));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Throws<InvalidDataException>(() => store.Save(path, Array.Empty<DecodedShard>()));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void CorruptMiddleFrameFailsRatherThanReturningAValidatedPrefix()
    {
        using var tmp = new TempDir();
        List<DecodedShard> shards = Shards(tmp);
        Assert.True(shards.Count >= 2);
        string path = tmp.File("middle.qrsession");
        new SessionStore().Save(path, shards.Take(2).ToArray());

        byte[] bytes = File.ReadAllBytes(path);
        int firstFrameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(9, 4));
        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(13, 4));
        int firstPayload = 17 + headerLength + 4;
        Assert.True(firstPayload < 9 + 4 + firstFrameLength);
        bytes[firstPayload] ^= 0x40;
        File.WriteAllBytes(path, bytes);
        byte[] corrupt = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => new SessionStore().Load(path));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
    }

    [Fact]
    public void TornFinalAppendRecoversOnlyValidatedPrefixAndRepairsOnSave()
    {
        using var tmp = new TempDir();
        List<DecodedShard> source = Shards(tmp);
        Assert.True(source.Count >= 2);
        string path = tmp.File("torn.qrsession");
        new SessionStore().Save(path, source.Take(2).ToArray());
        byte[] complete = File.ReadAllBytes(path);
        int firstFrameLength = BinaryPrimitives.ReadInt32LittleEndian(complete.AsSpan(9, 4));
        int firstFrameEnd = 9 + 4 + firstFrameLength + 4;
        File.WriteAllBytes(path, complete[..^7]);
        byte[] torn = File.ReadAllBytes(path);

        using (ISessionTransaction transaction = new SessionStore().Open(path))
        {
            Assert.Single(transaction.Shards);
            Assert.NotNull(transaction.RecoveryNotice);
            Assert.Equal(torn, File.ReadAllBytes(path)); // opening alone never truncates the evidence
            transaction.Save([.. transaction.Shards, source[1]]);
        }

        using ISessionTransaction repaired = new SessionStore().Open(path);
        Assert.Equal(2, repaired.Shards.Count);
        Assert.Null(repaired.RecoveryNotice);
        Assert.Equal(firstFrameEnd + (complete.Length - firstFrameEnd), new FileInfo(path).Length);
    }

    [Fact]
    public void CompleteDecodeNeverDeletesAForeignSessionPath()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("one.bin", TestData.Random(1_000));
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("one-shard"), Fast);
        Assert.Single(encoded.Files);
        byte[] sentinel = TestData.Random(20_000, seed: 91);
        string foreign = tmp.WriteFile("valuable.bin", sentinel);

        var result = Run("decode", encoded.Files[0], "--session", foreign, "-o", tmp.File("restored.bin"));

        Assert.Equal(1, result.Code);
        Assert.Contains("not a QrShard session", result.Error);
        Assert.Equal(sentinel, File.ReadAllBytes(foreign));
    }

    [Fact]
    public void ForeignFileCreatedOrReplacedDuringATransactionIsNeverOverwrittenOrDeleted()
    {
        using var tmp = new TempDir();
        List<DecodedShard> source = Shards(tmp);
        Assert.True(source.Count >= 2);
        var store = new SessionStore();

        string createdLate = tmp.File("created-late.qrsession");
        using (ISessionTransaction transaction = store.Open(createdLate))
        {
            byte[] sentinel = TestData.Random(7_000, seed: 201);
            File.WriteAllBytes(createdLate, sentinel);
            Assert.Throws<IOException>(() => transaction.Save([source[0]]));
            Assert.Equal(sentinel, File.ReadAllBytes(createdLate));
        }

        string replaced = tmp.File("replaced.qrsession");
        store.Save(replaced, [source[0]]);
        using (ISessionTransaction transaction = store.Open(replaced))
        {
            byte[] sentinel = TestData.Random(8_000, seed: 202);
            File.WriteAllBytes(replaced, sentinel);
            Assert.Throws<InvalidDataException>(() => transaction.Save([source[1]]));
            Assert.Throws<InvalidDataException>(() => transaction.Delete());
            Assert.Equal(sentinel, File.ReadAllBytes(replaced));
        }
    }

    [Fact]
    public void SameLengthReplacementWithAValidSessionPrefixIsNotDeleted()
    {
        using var tmp = new TempDir();
        DecodedShard shard = Shards(tmp, bytes: 10_000)[0];
        string path = tmp.File("identity.qrsession");
        var store = new SessionStore();
        store.Save(path, [shard]);
        byte[] original = File.ReadAllBytes(path);

        using ISessionTransaction transaction = store.Open(path);
        byte[] replacement = TestData.Random(original.Length, seed: 7331);
        original.AsSpan(0, 9).CopyTo(replacement); // valid QRSS v2 prefix and format CRC
        File.WriteAllBytes(path, replacement);

        Assert.Throws<InvalidDataException>(() => transaction.Delete());
        Assert.True(File.Exists(path));
        Assert.Equal(replacement, File.ReadAllBytes(path));
    }

    [Fact]
    public void MixedMetadataForOneFileIdIsRejectedBeforeSessionPublication()
    {
        using var tmp = new TempDir();
        List<DecodedShard> source = Shards(tmp);
        Assert.True(source.Count >= 2);
        DecodedShard second = source[1];
        var inconsistentHeader = new ShardHeader
        {
            FileId = second.Header.FileId,
            Index = second.Header.Index,
            Count = checked(second.Header.Count + 1),
            PayloadLength = second.Header.PayloadLength,
            PayloadCrc32 = second.Header.PayloadCrc32,
            TotalLength = second.Header.TotalLength,
            OriginalLength = second.Header.OriginalLength,
            Flags = second.Header.Flags,
            Sha256 = second.Header.Sha256,
            FileName = second.Header.FileName,
            StripeData = second.Header.StripeData,
            StripeParity = second.Header.StripeParity,
        };
        var inconsistent = second with { Header = inconsistentHeader };
        string path = tmp.File("mixed.qrsession");

        Assert.Throws<InvalidDataException>(() =>
            new SessionStore().Save(path, [source[0], inconsistent]));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void NormalizedSessionOutputAndCaptureAliasesAreRejectedBeforeMutation()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("alias.bin", TestData.Random(1_000));
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("alias-shards"), Fast);
        string capture = encoded.Files[0];
        byte[] captureBefore = File.ReadAllBytes(capture);
        string session = tmp.File("state.qrsession");
        string normalizedSessionAlias = Path.Combine(tmp.Path, "child", "..", "state.qrsession");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "child"));

        var sameSessionAndOutput = Run("decode", capture, "--session", session,
            "-o", normalizedSessionAlias);
        Assert.Equal(1, sameSessionAndOutput.Code);
        Assert.Contains("different paths", sameSessionAndOutput.Error);
        Assert.False(File.Exists(session));

        var sessionAliasesCapture = Run("decode", capture, "--session", capture,
            "-o", tmp.File("other.bin"));
        Assert.Equal(1, sessionAliasesCapture.Code);
        Assert.Contains("input capture", sessionAliasesCapture.Error);
        Assert.Equal(captureBefore, File.ReadAllBytes(capture));

        var outputAliasesCapture = Run("decode", capture, "--session", session, "-o", capture);
        Assert.Equal(1, outputAliasesCapture.Code);
        Assert.Contains("input capture", outputAliasesCapture.Error);
        Assert.Equal(captureBefore, File.ReadAllBytes(capture));
    }

    [Fact]
    public void ClipboardAliasIsRejectedBeforePlatformOrClipboardAccess()
    {
        using var tmp = new TempDir();
        string session = tmp.File("clipboard.qrsession");
        string alias = Path.Combine(tmp.Path, ".", "clipboard.qrsession");

        var result = Run("decode", "--clipboard", "--session", session, "-o", alias);

        Assert.Equal(1, result.Code);
        Assert.Contains("different paths", result.Error);
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void SessionLeaseSidecarCannotAliasCaptureOrOutput()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        string capture = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast).Files[0];
        string session = tmp.File("state.qrsession");
        string lease = session + ".lock";
        File.Copy(capture, lease);
        byte[] before = File.ReadAllBytes(lease);

        var asInput = Run("decode", lease, "--session", session, "-o", tmp.File("output.bin"));
        Assert.Equal(1, asInput.Code);
        Assert.Contains("lease path", asInput.Error);
        Assert.Equal(before, File.ReadAllBytes(lease));
        Assert.False(File.Exists(session));

        var asOutput = Run("decode", capture, "--session", session, "-o", lease);
        Assert.Equal(1, asOutput.Code);
        Assert.Contains("lease path", asOutput.Error);
        Assert.Equal(before, File.ReadAllBytes(lease));
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void SessionRestoreRefusesToOverwriteAnyExistingDestination()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"), Fast);
        string session = tmp.File("state.qrsession");
        byte[] sentinel = TestData.Random(257, seed: 543);
        string output = tmp.WriteFile("existing.bin", sentinel);

        var result = Run("decode", encoded.Files[0], "--session", session, "-o", output);

        Assert.Equal(1, result.Code);
        Assert.Contains("fresh path", result.Error);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void ExclusiveLeasePreventsConcurrentLostUpdates()
    {
        using var tmp = new TempDir();
        List<DecodedShard> source = Shards(tmp);
        Assert.True(source.Count >= 2);
        string path = tmp.File("leased.qrsession");
        var store = new SessionStore();

        using (ISessionTransaction first = store.Open(path))
        {
            IOException busy = Assert.Throws<IOException>(() => store.Open(path));
            Assert.Contains("exclusive lease", busy.Message);
            first.Save([source[0]]);
        }
        using (ISessionTransaction second = store.Open(path))
            second.Save([.. second.Shards, source[1]]);

        Assert.Equal(2, store.Load(path).Count);
    }

    [Fact]
    public void CompleteSessionCanBeRetriedWithoutAnyCaptureArguments()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(1_000);
        string input = tmp.WriteFile("retry.bin", content);
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("retry-shards"),
            Fast with { Password = "correct" });
        string session = tmp.File("complete.qrsession");
        new SessionStore().Save(session, new ShardDecoder().CollectShards(encoded.Files, _ => { }));
        string output = tmp.File("restored.bin");

        var result = Run("decode", "--session", session, "-o", output, "-p", "correct");

        Assert.Equal(0, result.Code);
        Assert.Equal(content, File.ReadAllBytes(output));
        Assert.False(File.Exists(session));
    }

    [Fact]
    public void DuplicateFramesAreDeduplicatedAndBudgetIsEnforcedBeforePublication()
    {
        using var tmp = new TempDir();
        DecodedShard shard = Shards(tmp, bytes: 10_000)[0];
        string dedup = tmp.File("dedup.qrsession");
        var repeated = Enumerable.Repeat(shard, 50_000).ToArray();

        new SessionStore().Save(dedup, repeated);

        Assert.Single(new SessionStore().Load(dedup));
        Assert.True(new FileInfo(dedup).Length < shard.Payload.Length * 2L + 16_384);

        string overBudget = tmp.File("budget.qrsession");
        var limited = new SessionStore(maxStoredBytes: 128);
        Assert.Throws<InvalidDataException>(() => limited.Save(overBudget, [shard]));
        Assert.False(File.Exists(overBudget));
    }

    [Fact]
    public void ManyTinyShardsCannotCreateASessionThatTheSameDecodeBudgetCannotResume()
    {
        using var tmp = new TempDir();
        const int total = 2_500;
        byte[] wholeSha = SHA256.HashData(new byte[total]);
        var crc = new Crc();
        var tiny = new List<DecodedShard>(total);
        for (int i = 0; i < total; i++)
        {
            byte[] payload = [(byte)i];
            var header = new ShardHeader
            {
                FileId = 0x51525354, Index = i, Count = total,
                PayloadLength = 1, PayloadCrc32 = crc.Crc32(payload),
                TotalLength = total, OriginalLength = total, Flags = 0,
                Sha256 = wholeSha, FileName = "tiny.bin",
            };
            tiny.Add(new DecodedShard(header, payload, "capture", 0, 0));
        }

        const long budgetBytes = 1_000_000;
        string acceptedPath = tmp.File("accepted-tiny.qrsession");
        var store = new SessionStore(maxStoredBytes: budgetBytes);
        store.Save(acceptedPath, tiny.Take(1_200).ToArray());
        List<DecodedShard> loaded = store.Load(acceptedPath);
        Assert.Equal(1_200, loaded.Count);

        // Session admission and restart use the same retained-object/count model. Anything the
        // store accepts must therefore seed the decoder's equal 1 MB budget without refusal.
        var resumeBudget = new ShardDecoder.SuccessfulShardRetentionBudget(1);
        resumeBudget.Seed(loaded);
        Assert.Equal(1_200, resumeBudget.RetainedCount);

        string rejectedPath = tmp.File("rejected-tiny.qrsession");
        Assert.Throws<InvalidDataException>(() =>
            new SessionStore(maxStoredBytes: budgetBytes).Save(rejectedPath, tiny));
        Assert.False(File.Exists(rejectedPath));
    }

    [Fact]
    public void ConflictingValidCandidatesBecomeADurableTerminalErasure()
    {
        using var tmp = new TempDir();
        DecodedShard original = Shards(tmp, bytes: 10_000)[0];
        byte[] differentPayload = (byte[])original.Payload.Clone();
        differentPayload[differentPayload.Length / 2] ^= 0x5a;
        var differentHeader = new ShardHeader
        {
            FileId = original.Header.FileId,
            Index = original.Header.Index,
            Count = original.Header.Count,
            PayloadLength = differentPayload.Length,
            PayloadCrc32 = new Crc().Crc32(differentPayload),
            TotalLength = original.Header.TotalLength,
            OriginalLength = original.Header.OriginalLength,
            Flags = original.Header.Flags,
            Sha256 = original.Header.Sha256,
            FileName = original.Header.FileName,
            StripeData = original.Header.StripeData,
            StripeParity = original.Header.StripeParity,
        };
        var conflicting = original with { Header = differentHeader, Payload = differentPayload };
        string path = tmp.File("conflict.qrsession");
        var store = new SessionStore();

        store.Save(path, [original]);
        store.Save(path, [conflicting]);

        Assert.Empty(store.Load(path));
        long afterConflict = new FileInfo(path).Length;

        // Neither the original nor a third candidate is allowed to win after a restart.  Exact
        // repeats also do not grow the append-only journal indefinitely.
        store.Save(path, [original, conflicting]);
        Assert.Empty(store.Load(path));
        Assert.Equal(afterConflict, new FileInfo(path).Length);
    }

    [Fact]
    public void ConflictRecordsCannotGrowTheJournalPastItsPhysicalBudget()
    {
        using var tmp = new TempDir();
        byte[] tinyPayload = [7];
        var tinyHeader = new ShardHeader
        {
            FileId = 77, Index = 0, Count = 1, PayloadLength = tinyPayload.Length,
            PayloadCrc32 = new Crc().Crc32(tinyPayload), TotalLength = 1, OriginalLength = 1,
            Flags = 0, Sha256 = System.Security.Cryptography.SHA256.HashData(tinyPayload),
            FileName = "x.bin",
        };
        var tiny = new DecodedShard(tinyHeader, tinyPayload, "tiny", 0, 0);
        byte[] hugePayload = new byte[1024 * 1024];
        var hugeHeader = new ShardHeader
        {
            FileId = tinyHeader.FileId, Index = 0, Count = 1, PayloadLength = hugePayload.Length,
            PayloadCrc32 = new Crc().Crc32(hugePayload), TotalLength = tinyHeader.TotalLength,
            OriginalLength = tinyHeader.OriginalLength, Flags = tinyHeader.Flags,
            Sha256 = tinyHeader.Sha256, FileName = tinyHeader.FileName,
        };
        var hugeConflict = new DecodedShard(hugeHeader, hugePayload, "huge", 0, 0);
        var store = new SessionStore(maxStoredBytes: 1_024);
        string path = tmp.File("bounded.qrsession");
        store.Save(path, [tiny]);
        store.Save(path, [hugeConflict]);

        Assert.Empty(store.Load(path));
        Assert.True(new FileInfo(path).Length <= 9 + 3 * 1_024,
            $"Conflict journal grew to {new FileInfo(path).Length:N0} bytes.");
    }

    [Fact]
    public void CliPersistsAConflictAndNeverLetsAThirdCopyWinAfterRestart()
    {
        using var tmp = new TempDir();
        byte[] content = TestData.Random(150_000, seed: 881);
        string input = tmp.WriteFile("source.bin", content);
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("encoded"), Fast);
        Assert.True(encoded.DataImages >= 2);
        var decoder = new ShardDecoder();
        DecodedShard original = decoder.DecodeImage(encoded.Files[0]);
        byte[] poisonPayload = (byte[])original.Payload.Clone();
        poisonPayload[poisonPayload.Length / 2] ^= 0xa5;
        var poisonHeader = new ShardHeader
        {
            FileId = original.Header.FileId, Index = original.Header.Index,
            Count = original.Header.Count, PayloadLength = poisonPayload.Length,
            PayloadCrc32 = new Crc().Crc32(poisonPayload), TotalLength = original.Header.TotalLength,
            OriginalLength = original.Header.OriginalLength, Flags = original.Header.Flags,
            Sha256 = original.Header.Sha256, FileName = original.Header.FileName,
            StripeData = original.Header.StripeData, StripeParity = original.Header.StripeParity,
        };
        string poisonImage = tmp.File("poison.png");
        Render(poisonHeader, poisonPayload, decoder.Diagnose(encoded.Files[0]).Layout!, poisonImage);
        string session = tmp.File("state.qrsession");
        string output = tmp.File("restored.bin");

        // The counterfeit plus every other part is superficially complete, but the final SHA
        // catches it. The validated captures still persist for a later retry.
        string[] firstInputs = [poisonImage, .. encoded.Files.Skip(1)];
        var first = Run(["decode", .. firstInputs, "--session", session, "-o", output]);
        Assert.Equal(1, first.Code);
        Assert.True(File.Exists(session));
        Assert.False(File.Exists(output));

        // The genuine candidate disagrees, so both become a durable erasure instead of the first
        // or last copy winning. A third genuine copy after another open remains ignored.
        var second = Run("decode", encoded.Files[0], "--session", session, "-o", output, "--json");
        Assert.Equal(3, second.Code);
        Assert.Contains("terminal erasure", second.Error);
        Assert.Contains("\"terminalConflicts\": 1", second.Out);
        Assert.False(File.Exists(output));

        var third = Run("decode", encoded.Files[0], "--session", session, "-o", output, "--json");
        Assert.Equal(3, third.Code);
        Assert.Contains("\"terminalConflicts\": 1", third.Out);
        Assert.False(File.Exists(output));

        var verify = Run("verify", encoded.Files[0], "--session", session, "--json");
        Assert.Equal(3, verify.Code);
        Assert.Contains("\"terminalConflicts\": 1", verify.Out);
    }

    [Fact]
    public void LegacyV1MigratesAtomicallyAndPreservesEveryUniqueShard()
    {
        using var tmp = new TempDir();
        List<DecodedShard> source = Shards(tmp).Take(2).ToList();
        string path = tmp.File("legacy.qrsession");
        WriteLegacy(path, source);
        Assert.Equal(1, File.ReadAllBytes(path)[4]);

        List<DecodedShard> loaded = new SessionStore().Load(path);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, File.ReadAllBytes(path)[4]);
        Assert.Equal(source.Select(s => s.Payload), loaded.Select(s => s.Payload));
        Assert.Equal(2, new SessionStore().Load(path).Count);
    }

    [Fact]
    public void LegacyCountAboveLimitFailsExplicitlyWithoutSilentMillionEntryTruncation()
    {
        using var tmp = new TempDir();
        string path = tmp.File("too-many.qrsession");
        using (var fs = File.Create(path))
        using (var writer = new BinaryWriter(fs))
        {
            writer.Write("QRSS"u8);
            writer.Write((byte)1);
            writer.Write(SessionStore.MaxEntries + 1);
        }
        byte[] before = File.ReadAllBytes(path);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SessionStore().Load(path));

        Assert.Contains("1,000,001", error.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static void WriteLegacy(string path, IReadOnlyCollection<DecodedShard> shards)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);
        writer.Write("QRSS"u8);
        writer.Write((byte)1);
        writer.Write(shards.Count);
        foreach (DecodedShard shard in shards)
        {
            byte[] header = shard.Header.Serialize();
            writer.Write(header.Length);
            writer.Write(header);
            writer.Write(shard.Payload.Length);
            writer.Write(shard.Payload);
        }
    }

    private static void Render(ShardHeader header, byte[] payload, Layout layout, string path)
    {
        byte[] headerBytes = header.Serialize();
        byte[] stream = new byte[headerBytes.Length + payload.Length];
        headerBytes.CopyTo(stream, 0);
        payload.CopyTo(stream, headerBytes.Length);
        var renderer = new ShardRenderer();
        renderer.RenderShard(layout, new Palette().Build(layout.BitsPerCell), layout.PackMetadata(),
            stream, stream.Length, path, new RenderScratch(layout),
            renderer.CreateWriter("png", layout, AppSettings.BuiltIn));
    }
}
