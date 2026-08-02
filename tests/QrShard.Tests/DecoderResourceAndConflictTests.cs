using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using QrShard;

namespace QrShard.Tests;

/// <summary>Adversarial bounds and duplicate-integrity tests for decode/reassembly paths.</summary>
[Collection(CurrentDirectoryCollection.Name)]
public class DecoderResourceAndConflictTests
{
    private static Layout FusionLayout(int variant = 0) => new()
    {
        BitsPerCell = 1,
        CellPx = 1,
        GridW = 255 + variant,
        GridH = 8,
        MetaH = 1,
        InnerW = 300 + variant,
        InnerH = 32,
        EccParity = 16,
        FinderModule = 0,
    };

    private static ShardHeader Header(byte[] payload, byte[] whole, int index, byte flags,
        int count = 2, int stripeData = 2, int stripeParity = 1,
        ulong fileId = 0xDEC0DE, string fileName = "conflict.bin") => new()
    {
        FileId = fileId,
        Index = index,
        Count = count,
        PayloadLength = payload.Length,
        PayloadCrc32 = new Crc().Crc32(payload),
        TotalLength = whole.Length,
        OriginalLength = whole.Length,
        Flags = flags,
        Sha256 = SHA256.HashData(whole),
        FileName = fileName,
        StripeData = stripeData,
        StripeParity = stripeParity,
    };

    private static DecodedShard Shard(byte[] payload, byte[] whole, int index, byte flags,
        int count = 2, int stripeData = 2, int stripeParity = 1, string source = "capture",
        ulong fileId = 0xDEC0DE, string fileName = "conflict.bin") =>
        new(Header(payload, whole, index, flags, count, stripeData, stripeParity, fileId, fileName),
            payload, source, 0, 0);

    [Fact]
    public void ParseDib_RejectsIntMinHeightWithoutOverflow()
    {
        var dib = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), int.MinValue);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);

        Assert.Null(ClipboardReader.ParseDib(dib));
    }

    [Fact]
    public void ParseDib_AppliesDecodeBudgetBeforeAllocatingClipboardPixels()
    {
        var dib = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 5_000);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 5_000);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);

        var ex = Assert.Throws<ShardDecodeException>(() =>
            ClipboardReader.ParseDib(dib, decodeMemoryBudgetMB: 64));

        Assert.Contains("DecodeMemoryBudgetMB", ex.Message);
    }

    [Fact]
    public void FolderDecodeLogs_SanitizeHostileFilesystemBasename()
    {
        string hostile = "capture-\u001b[2J\r\nforged.png";
        var log = new List<string>();

        Assert.Empty(new ShardDecoder().CollectShards([hostile], log.Add));

        Assert.Contains(log, line => line.Contains("FAILED"));
        Assert.All(log, line =>
        {
            Assert.DoesNotContain('\u001b', line);
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\n', line);
        });
    }

    [Fact]
    public void SparseHugeNoParityGeometry_FailsBeforeCountSizedAllocation()
    {
        byte[] payload = [1];
        byte[] declaredWhole = new byte[5_000_000];
        var one = Shard(payload, declaredWhole, index: 0, flags: 0,
            count: 5_000_000, stripeData: 0, stripeParity: 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([one], null, _ => { }));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"sparse no-parity failure allocated {allocated:N0} bytes");
        Assert.True(ex.Message.Length < 1_000, "human diagnostic must stay capped");
        Assert.Contains("4,999,999 total", ex.Message);
        Assert.Contains(", ...", ex.Message);
    }

    [Fact]
    public void SparseHugeParityGeometry_FailsBeforeDenseOrdinalArrays()
    {
        byte[] payload = [1];
        byte[] declaredWhole = new byte[5_000_000];
        var one = Shard(payload, declaredWhole, index: 0, flags: 0,
            count: 5_000_000, stripeData: 254, stripeParity: 1);
        var parity = new ParityReassembler();

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<ShardDecodeException>(() =>
            parity.ReassembleWithParity([one], one.Header, _ => { }, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"sparse parity failure allocated {allocated:N0} bytes");
        Assert.True(ex.Message.Length < 1_000, "human diagnostic must stay capped");
        Assert.Contains("beyond parity recovery", ex.Message);
    }

    [Fact]
    public void RecoveryGeometry_RejectsOversizedFountainAndCauchyWidthsAtAllEntryPoints()
    {
        byte[] payload = [1];
        var fountain = Shard(payload, payload, 0, ShardHeader.FlagFountain,
            count: 1, stripeData: 2, stripeParity: 1);
        Assert.Null(ShardHeader.Deserialize(fountain.Header.Serialize(), out _));
        Assert.False(new ParityReassembler().IsSetComplete([fountain]));
        Assert.Throws<ShardDecodeException>(() => new ParityReassembler().ReassembleWithParity(
            [fountain], fountain.Header, _ => { }, out _));

        var cauchy = Shard(payload, payload, 0, 0,
            count: 1, stripeData: 1, stripeParity: CrossShardFec.MaxShardsPerStripe);
        Assert.Null(ShardHeader.Deserialize(cauchy.Header.Serialize(), out _));
        Assert.False(new ParityReassembler().IsSetComplete([cauchy]));
        Assert.Throws<ShardDecodeException>(() => new ParityReassembler().ReassembleWithParity(
            [cauchy], cauchy.Header, _ => { }, out _));

        var halfSpecified = Shard(payload, payload, 0, 0,
            count: 1, stripeData: 1, stripeParity: 0);
        Assert.Null(ShardHeader.Deserialize(halfSpecified.Header.Serialize(), out _));
        Assert.False(new ParityReassembler().IsSetComplete([halfSpecified]));
        Assert.Throws<ShardDecodeException>(() => new ShardAssembler().Assemble(
            [halfSpecified], null, _ => { }));
    }

    [Fact]
    public void ConflictingDataCopies_AreErasuresRecoveredByParity()
    {
        using var tmp = new TempDir();
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] a = whole[..4], b = whole[4..];
        byte[] poison = [9, 9, 9, 9];
        byte[] parity = new CrossShardFec().Encode([a, b], parityCount: 1, shardLen: 4)[0];
        var shards = new List<DecodedShard>
        {
            Shard(a, whole, 0, 0, source: "correct-a"),
            Shard(poison, whole, 0, 0, source: "conflicting-a"),
            Shard(b, whole, 1, 0, source: "b"),
            Shard(parity, whole, 0, ShardHeader.FlagParity, source: "parity"),
        };
        string output = tmp.File("restored.bin");

        new ShardAssembler().Assemble(shards, output, _ => { });

        Assert.Equal(whole, File.ReadAllBytes(output));
    }

    [Fact]
    public void ConflictingDataCopies_WithDifferentLengthsAreStillParityErasures()
    {
        using var tmp = new TempDir();
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] a = whole[..4], b = whole[4..];
        byte[] poison = [9, 9, 9];
        byte[] parity = new CrossShardFec().Encode([a, b], parityCount: 1, shardLen: 4)[0];
        var shards = new List<DecodedShard>
        {
            Shard(a, whole, 0, 0, source: "correct-a"),
            Shard(poison, whole, 0, 0, source: "short-conflicting-a"),
            Shard(b, whole, 1, 0, source: "b"),
            Shard(parity, whole, 0, ShardHeader.FlagParity, source: "parity"),
        };
        string output = tmp.File("restored-different-length.bin");

        new ShardAssembler().Assemble(shards, output, _ => { });

        Assert.Equal(whole, File.ReadAllBytes(output));
    }

    [Fact]
    public void ConflictingDataCopies_WithoutParityFailExplicitlyInsteadOfFirstWins()
    {
        byte[] whole = [1, 2, 3, 4];
        var correct = Shard(whole, whole, 0, 0, count: 1, stripeData: 0, stripeParity: 0);
        var poison = Shard([9, 9, 9, 9], whole, 0, 0, count: 1, stripeData: 0, stripeParity: 0);

        var ex = Assert.Throws<ShardDecodeException>(() =>
            new ShardAssembler().Assemble([correct, poison], null, _ => { }));

        Assert.Contains("conflicting CRC-valid copies", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ByteIdenticalDuplicates_RemainHarmless()
    {
        using var tmp = new TempDir();
        byte[] whole = [1, 2, 3, 4];
        var first = Shard(whole, whole, 0, 0, count: 1, stripeData: 0, stripeParity: 0);
        var duplicate = Shard([.. whole], whole, 0, 0, count: 1, stripeData: 0, stripeParity: 0);
        string output = tmp.File("duplicate.bin");

        new ShardAssembler().Assemble([first, duplicate], output, _ => { });

        Assert.Equal(whole, File.ReadAllBytes(output));
    }

    [Fact]
    public void MixedFamilies_PreflightIncompleteSiblingBeforePublishingCompleteFile()
    {
        using var tmp = new TempDir();
        string cwd = Environment.CurrentDirectory;
        string outputDirectory = tmp.Sub("output");
        Environment.CurrentDirectory = outputDirectory;
        try
        {
            var complete = Shard([1], [1], 0, 0, count: 1, stripeData: 0, stripeParity: 0,
                fileId: 1, fileName: "complete.bin");
            var incomplete = Shard([2], [2, 3], 0, 0, count: 2, stripeData: 0, stripeParity: 0,
                fileId: 2, fileName: "incomplete.bin");

            var ex = Assert.Throws<ShardDecodeException>(() =>
                new ShardAssembler().Assemble([complete, incomplete], null, _ => { }));

            Assert.Contains("incomplete or inconsistent", ex.Message);
            Assert.False(File.Exists(Path.Combine(outputDirectory, "complete.bin")),
                "preflight failure must happen before any sibling is published");
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    [Fact]
    public void PublicDecodeImages_MixedFamiliesDoesNotPartiallyPublish()
    {
        using var tmp = new TempDir();
        string firstInput = tmp.WriteFile("complete-public.bin", TestData.Random(5_000, seed: 11));
        string secondInput = tmp.WriteFile("incomplete-public.bin", TestData.Random(200_000, seed: 12));
        var codec = new QrShardCodec();
        var options = new QrShardEncodeOptions { Width = 900, Height = 900 };
        QrShardEncodeReport first = codec.EncodeFile(firstInput, tmp.Sub("first-shards"), options);
        QrShardEncodeReport second = codec.EncodeFile(secondInput, tmp.Sub("second-shards"), options);
        Assert.True(second.Files.Count > 1);

        string cwd = Environment.CurrentDirectory;
        string outputDirectory = tmp.Sub("public-output");
        Environment.CurrentDirectory = outputDirectory;
        try
        {
            Assert.Throws<QrShardDecodeException>(() =>
                codec.DecodeImages(first.Files.Concat(second.Files.Take(1))));
            Assert.False(File.Exists(Path.Combine(outputDirectory, "complete-public.bin")));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    [Fact]
    public void ConflictingParityAlternatives_AreBoundedAndCannotCompleteASet()
    {
        byte[] whole = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] a = whole[..4], b = whole[4..];
        byte[] parityPayload = new CrossShardFec().Encode([a, b], 1, 4)[0];
        var shards = new List<DecodedShard> { Shard(a, whole, 0, 0) };
        shards.Add(Shard(parityPayload, whole, 0, ShardHeader.FlagParity));
        for (int i = 0; i < 50_000; i++)
            shards.Add(Shard([(byte)(i + 1), 7, 7, 7], whole, 0, ShardHeader.FlagParity));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(new ParityReassembler().IsSetComplete(shards));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 3_000_000, $"conflicting alternatives allocated {allocated:N0} bytes");
    }

    [Fact]
    public void FountainSolver_StopsEnumeratingSurplusRowsAtFullRank()
    {
        int rankRows = 0;
        IEnumerable<byte[]> RankRows()
        {
            for (int i = 0; i < 100_000; i++)
            {
                rankRows++;
                yield return [1];
            }
        }
        Assert.Equal(1, new FountainFec().Rank(RankRows(), dataCount: 1));
        Assert.Equal(1, rankRows);

        int reconstructionRows = 0;
        IEnumerable<(byte[] Coef, byte[] Payload)> ReconstructionRows()
        {
            for (int i = 0; i < 100_000; i++)
            {
                reconstructionRows++;
                yield return ([1], [42]);
            }
        }
        Assert.True(new FountainFec().TryReconstruct(
            ReconstructionRows(), dataCount: 1, shardLen: 1, out byte[][] data));
        Assert.Equal(1, reconstructionRows);
        Assert.Equal(42, data[0][0]);
    }

    [Fact]
    public void SessionStatus_ReportsExactMissingTotalWithBoundedOrdinalSample()
    {
        byte[] declaredWhole = new byte[5_000_000];
        byte[] payload = [1];
        var session = new QrShardDecodeSession();
        Assert.True(session.Add(Shard(payload, declaredWhole, 0, 0,
            count: 5_000_000, stripeData: 0, stripeParity: 0)).Accepted);

        QrShardFileStatus status = Assert.Single(session.Status());
        Assert.Equal(4_999_999, status.MissingImageCount);
        Assert.Equal(QrShardDecodeSession.MaxReportedMissingImages, status.MissingImages.Count);
        Assert.True(status.MissingImagesTruncated);

        var conflict = session.Add(Shard([2], declaredWhole, 0, 0,
            count: 5_000_000, stripeData: 0, stripeParity: 0));
        Assert.False(conflict.Accepted);
        Assert.Contains("Conflicting", conflict.Error);
        status = Assert.Single(session.Status());
        Assert.Equal(0, status.DataPresent);
        Assert.Equal(5_000_000, status.MissingImageCount);
    }

    [Fact]
    public void PublicDecodeSession_RefusesOversizedShardWithoutRetainingItsFamily()
    {
        var session = new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: 1);
        byte[] whole = [1];
        var refused = session.Add(Shard(new byte[1_000_000], whole, 0, 0,
            count: 1, stripeData: 0, stripeParity: 0, source: "oversized",
            fileId: 1, fileName: "refused.bin"));

        Assert.False(refused.Accepted);
        Assert.Contains("session budget", refused.Error);

        var accepted = session.Add(Shard([2], whole, 0, 0,
            count: 1, stripeData: 0, stripeParity: 0, source: "x",
            fileId: 2, fileName: "accepted.bin"));
        Assert.True(accepted.Accepted);
        Assert.Equal("accepted.bin", Assert.Single(session.Status()).FileName);
    }

    [Fact]
    public void PublicDecodeSession_BoundsTinyUniqueShardsAndStillAcceptsDuplicates()
    {
        var session = new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: 1);
        byte[] whole = [3];
        int limit = ShardDecoder.SuccessfulShardRetentionBudget
            .MaximumInputCountForByteLimit(1_000_000);

        for (int index = 0; index < limit; index++)
            Assert.True(session.Add(Shard([4], whole, index, 0,
                count: limit + 1, stripeData: 0, stripeParity: 0, source: "x")).Accepted);

        QrShardAddResult refused = session.Add(Shard([4], whole, limit, 0,
            count: limit + 1, stripeData: 0, stripeParity: 0, source: "x"));
        Assert.False(refused.Accepted);
        Assert.Contains("count limit", refused.Error);

        QrShardAddResult duplicate = session.Add(Shard([4], whole, 0, 0,
            count: limit + 1, stripeData: 0, stripeParity: 0, source: "duplicate"));
        Assert.True(duplicate.Accepted);
        Assert.False(duplicate.WasNew);
    }

    [Fact]
    public void PublicDecodeSession_ConflictReleasesPayloadButKeepsATerminalTombstone()
    {
        var session = new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: 1);
        byte[] whole = [5];
        Assert.True(session.Add(Shard(new byte[900_000], whole, 0, 0,
            count: 2, stripeData: 0, stripeParity: 0, source: "x")).Accepted);

        QrShardAddResult conflict = session.Add(Shard(
            Enumerable.Repeat((byte)1, 900_000).ToArray(), whole, 0, 0,
            count: 2, stripeData: 0, stripeParity: 0, source: "x"));
        Assert.False(conflict.Accepted);
        Assert.Contains("Conflicting", conflict.Error);

        Assert.True(session.Add(Shard(new byte[900_000], whole, 1, 0,
            count: 2, stripeData: 0, stripeParity: 0, source: "x")).Accepted);
        QrShardAddResult thirdCopy = session.Add(Shard([9], whole, 0, 0,
            count: 2, stripeData: 0, stripeParity: 0, source: "x"));
        Assert.False(thirdCopy.Accepted);
        Assert.Contains("Conflicting", thirdCopy.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void PublicDecodeSession_RejectsInvalidRetentionBudgets(int budgetMB)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: budgetMB));
    }

    [Fact]
    public void FailedCaptureRetention_IsBoundedByBudgetAndPerLayout()
    {
        var perLayout = new ShardDecoder.FailedCaptureRetentionBudget(decodeMemoryBudgetMB: 64);
        Layout layout = FusionLayout();
        for (int i = 0; i < PhotoFusion.MaxCapturesPerGroup; i++)
            Assert.True(perLayout.TryReserve(layout, 100));
        Assert.False(perLayout.TryReserve(layout, 100));

        var total = new ShardDecoder.FailedCaptureRetentionBudget(decodeMemoryBudgetMB: 64);
        Assert.True(total.TryReserve(FusionLayout(1), 2_000_000));
        Assert.False(total.TryReserve(FusionLayout(2), 1_000_000)); // 3x charge; 64 MB / 8 = 8 MB
        Assert.Equal(2_000_000, total.RetainedBytes);
        Assert.Equal(6_000_000, total.ReservedBytes);
    }

    [Fact]
    public void SuccessfulShardRetention_IsBoundedByBudgetAndInputMetadata()
    {
        var retained = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB: 64);
        byte[] payload = new byte[1_000_000];
        byte[] whole = [9];
        SuccessfulShardAdmission first = retained.TryAdmit(
            Header(payload, whole, index: 0, flags: 0), payload, 63_999_900);
        Assert.Equal(SuccessfulShardAdmissionKind.Added, first.Kind);
        Assert.NotNull(first.Payload);

        // A folder full of byte-identical valid copies is harmless input, not a way to consume
        // the run budget before a later unique ordinal arrives.
        SuccessfulShardAdmission duplicate = retained.TryAdmit(
            Header(payload, whole, index: 0, flags: 0), payload, 63_999_900);
        Assert.Equal(SuccessfulShardAdmissionKind.Duplicate, duplicate.Kind);

        // One disagreement makes the ordinal terminal. It needs only a marker; retaining a
        // second 64 MB candidate would let conflicting-copy floods amplify memory.
        byte[] conflictingPayload = Enumerable.Repeat((byte)2, payload.Length).ToArray();
        long conflictSequence = retained.ConflictSequence;
        SuccessfulShardAdmission conflict = retained.TryAdmit(
            Header(conflictingPayload, whole, index: 0, flags: 0), conflictingPayload, 63_999_900);
        Assert.Equal(SuccessfulShardAdmissionKind.Conflict, conflict.Kind);
        retained.ReleaseBatchConflicts(conflictSequence);

        // Releasing the terminal ordinal's full payload admits useful later recovery material.
        Assert.Equal(SuccessfulShardAdmissionKind.Added, retained.TryAdmit(
            Header(payload, whole, index: 1, flags: 0), payload, 500_000).Kind);
        Assert.Equal(SuccessfulShardAdmissionKind.Refused, retained.TryAdmit(
            Header(payload, whole, index: 2, flags: 0), payload, 600_000).Kind);
        Assert.Equal(63_499_900, retained.RetainedBytes);
        Assert.Equal(2, retained.RetainedCount);
        Assert.Equal(1, retained.RefusedCount);

        Assert.Equal(125_000,
            ShardDecoder.SuccessfulShardRetentionBudget.MaximumInputCount(decodeMemoryBudgetMB: 64));
        Assert.Equal(8_000_000,
            ShardDecoder.SuccessfulShardRetentionBudget.InputMetadataByteLimit(decodeMemoryBudgetMB: 64));
    }

    [Fact]
    public void SuccessfulShardRefusal_DoesNotRetainAnUnadmittedFamily()
    {
        var retained = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB: 1);
        byte[] payload = [1];
        byte[] whole = [2];
        Assert.Equal(SuccessfulShardAdmissionKind.Added, retained.TryAdmit(
            Header(payload, whole, 0, 0, fileId: 1), payload, 999_999).Kind);

        Assert.Equal(SuccessfulShardAdmissionKind.Refused, retained.TryAdmit(
            Header(payload, whole, 0, 0, fileId: 2, fileName: "first-refused.bin"),
            payload, 2).Kind);
        // If the refused family leaked into the family map, the changed filename would now be
        // reported as InconsistentFamily instead of independently reaching the still-full budget.
        Assert.Equal(SuccessfulShardAdmissionKind.Refused, retained.TryAdmit(
            Header(payload, whole, 0, 0, fileId: 2, fileName: "second-refused.bin"),
            payload, 2).Kind);
        Assert.Equal(1, retained.RetainedCount);
        Assert.Equal(999_999, retained.RetainedBytes);
    }

    [Fact]
    public void SeededExternalPayload_RemainsChargedWhileConflictAndNewAdmissionRace()
    {
        var retained = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB: 1);
        byte[] whole = [7];
        byte[] firstPayload = new byte[900_000];
        var seeded = new DecodedShard(
            Header(firstPayload, whole, index: 0, flags: 0), firstPayload, "session", 0, 0);
        retained.Seed([seeded]);
        long before = retained.RetainedBytes;
        Assert.InRange(before, 900_001, 999_999);

        byte[] conflictingPayload = Enumerable.Repeat((byte)1, firstPayload.Length).ToArray();
        SuccessfulShardAdmission conflict = default;
        SuccessfulShardAdmission later = default;
        int laterCharge = checked((int)(1_000_000 - before + 1));
        Parallel.Invoke(
            () => conflict = retained.TryAdmit(
                Header(conflictingPayload, whole, index: 0, flags: 0), conflictingPayload, 999_999),
            () => later = retained.TryAdmit(
                Header([2], whole, index: 1, flags: 0), [2], laterCharge));

        Assert.Equal(SuccessfulShardAdmissionKind.Conflict, conflict.Kind);
        Assert.Equal(SuccessfulShardAdmissionKind.Refused, later.Kind);
        Assert.Equal(before, retained.RetainedBytes);
    }

    [Fact]
    public void InFlightDecoderPayload_IsNotCreditedUntilTheWorkerBarrier()
    {
        var retained = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB: 1);
        byte[] whole = [8];
        byte[] firstPayload = new byte[900_000];
        Assert.Equal(SuccessfulShardAdmissionKind.Added, retained.TryAdmit(
            Header(firstPayload, whole, 0, 0), firstPayload, 950_000).Kind);
        long before = retained.RetainedBytes;
        long conflictSequence = retained.ConflictSequence;
        byte[] conflictingPayload = Enumerable.Repeat((byte)1, firstPayload.Length).ToArray();
        SuccessfulShardAdmission conflict = default;
        SuccessfulShardAdmission later = default;

        Parallel.Invoke(
            () => conflict = retained.TryAdmit(
                Header(conflictingPayload, whole, 0, 0), conflictingPayload, 950_000),
            () => later = retained.TryAdmit(
                Header([4], whole, 1, 0), [4], 50_001));

        Assert.Equal(SuccessfulShardAdmissionKind.Conflict, conflict.Kind);
        Assert.Equal(SuccessfulShardAdmissionKind.Refused, later.Kind);
        Assert.Equal(before, retained.RetainedBytes);
        retained.ReleaseBatchConflicts(conflictSequence);
        Assert.Equal(before - firstPayload.Length, retained.RetainedBytes);
    }

    [Fact]
    public void InputMaterialization_ChargesActualPathLengthBeforeReadingTheRestOfTheSequence()
    {
        int enumerated = 0;

        IEnumerable<string> HostilePaths()
        {
            enumerated++;
            yield return new string('x', 70_000); // 140,064 bytes > the 125,000-byte allowance below
            enumerated++;
            throw new InvalidOperationException("the bounded collector enumerated past its refusal point");
        }

        var ex = Assert.Throws<ShardResourceLimitException>(() =>
            ShardDecoder.MaterializeInputPaths(HostilePaths(), decodeMemoryBudgetMB: 1));

        Assert.Equal(1, enumerated);
        Assert.Contains("image/path metadata allowance", ex.Message);
    }

    [Fact]
    public void SuccessfulRetentionRefusal_DoesNotRetryTheValidImageThroughCameraRectification()
    {
        using var tmp = new TempDir();
        string settingsPath = tmp.File("decode-budget.json");
        File.WriteAllText(settingsPath,
            """{ "DecodeMaxParallelism": 1, "DecodeMemoryBudgetMB": 64 }""");
        AppSettings settings = AppSettings.Load(settingsPath);
        var rectifier = new CountingRectifier();
        var decoder = new ShardDecoder(settings, rectifier,
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        string input = tmp.WriteFile("tiny.bin", TestData.Random(100));
        EncodeResult encoded = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 });
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(64);
        byte[] blocker = [1];
        Assert.Equal(SuccessfulShardAdmissionKind.Added, successful.TryAdmit(
            Header(blocker, blocker, 0, 0, fileId: ulong.MaxValue), blocker, 64_000_000).Kind);

        Assert.Throws<ShardResourceLimitException>(() =>
            decoder.CollectShards([encoded.Files[0]], _ => { }, successful));
        Assert.Equal(0, rectifier.TryRectifyCalls);
    }

    [Fact]
    public void SuccessfulRetentionFatalStop_BoundsWorkToAlreadyInFlightImages()
    {
        using var tmp = new TempDir();
        string settingsPath = tmp.File("decode-fatal-stop.json");
        File.WriteAllText(settingsPath,
            """{ "DecodeMaxParallelism": 2, "DecodeMemoryBudgetMB": 64 }""");
        AppSettings settings = AppSettings.Load(settingsPath);
        var decoder = new ShardDecoder(settings, new CameraRectifier(),
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        string input = tmp.WriteFile("tiny.bin", TestData.Random(100));
        string validPath = new ShardEncoder().Encode(input, tmp.Sub("shards"),
            new EncodeOptions { Width = 900, Height = 900, CellPx = 3, BitsPerCell = 4 }).Files[0];
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(64);
        byte[] blocker = [1];
        Assert.Equal(SuccessfulShardAdmissionKind.Added, successful.TryAdmit(
            Header(blocker, blocker, 0, 0, fileId: ulong.MaxValue), blocker, 64_000_000).Kind);
        var log = new List<string>();

        Assert.Throws<ShardResourceLimitException>(() => decoder.CollectShards(
            Enumerable.Repeat(validPath, 64), log.Add, successful));

        Assert.InRange(successful.RefusedCount, 1, 2);
        Assert.InRange(log.Count(line => line.Contains("FAILED", StringComparison.Ordinal)), 1, 2);
    }

    [Fact]
    public void CollectShards_CanonicalizesDuplicatesAndEmitsOneTypedConflictForSharedSessions()
    {
        using var tmp = new TempDir();
        Layout layout = Layout.Create(900, 900, 3, 4, 32);
        byte[] whole = TestData.Random(64, seed: 71);
        byte[] firstPayload = TestData.Random(64, seed: 72);
        byte[] conflictingPayload = TestData.Random(64, seed: 73);
        ShardHeader firstHeader = Header(firstPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1);
        ShardHeader conflictingHeader = Header(conflictingPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1);
        string firstPath = tmp.File("a.png");
        string conflictingPath = tmp.File("b.png");
        Render(firstHeader, firstPayload, layout, firstPath);
        Render(conflictingHeader, conflictingPayload, layout, conflictingPath);

        var decoder = new ShardDecoder();
        List<DecodedShard> duplicates = decoder.CollectShards(
            Enumerable.Repeat(firstPath, 20), _ => { });
        Assert.Single(duplicates);
        Assert.False(duplicates[0].IsTerminalConflict);

        // Ordinary folder assembly sees the conflicted ordinal as missing; no invalid marker can
        // leak to the assembler. The shared session/watch overload receives exactly one typed
        // compact marker so it can persist the terminal erasure durably.
        Assert.Empty(decoder.CollectShards([firstPath, conflictingPath], _ => { }));
        var shared = new ShardDecoder.SuccessfulShardRetentionBudget(64);
        DecodedShard marker = Assert.Single(decoder.CollectShards(
            [firstPath, conflictingPath], _ => { }, shared));
        Assert.True(marker.IsTerminalConflict);
        Assert.Empty(marker.Payload);
        Assert.Equal(firstHeader.FileId, marker.Header.FileId);
        Assert.Equal(firstHeader.Index, marker.Header.Index);
        Assert.True(shared.RetainedBytes <
            ShardDecoder.SuccessfulShardRetentionBudget.RetentionCharge(
                new DecodedShard(firstHeader, firstPayload, firstPath, layout.EccParity, 0)));

        string sessionPath = tmp.File("typed-conflict.qrsession");
        using (ISessionTransaction transaction = new SessionStore().Open(sessionPath))
        {
            transaction.Save([marker]);
            Assert.Empty(transaction.Shards);
            Assert.Equal(1, transaction.ConflictedShardCount);
        }
        using (ISessionTransaction reopened = new SessionStore().Open(sessionPath))
        {
            Assert.Empty(reopened.Shards);
            Assert.Equal(1, reopened.ConflictedShardCount);
            reopened.Save([new DecodedShard(firstHeader, firstPayload, "third-copy", layout.EccParity, 0)]);
            Assert.Empty(reopened.Shards); // a third copy can never select a winner
            Assert.Equal(1, reopened.ConflictedShardCount);
        }

        string sameInvocationSession = tmp.File("same-invocation-conflict.qrsession");
        int decodeCode = new Cli().Run(
            ["decode", firstPath, conflictingPath, "--session", sameInvocationSession, "--json"],
            new StringWriter(), new StringWriter());
        Assert.Equal(3, decodeCode);
        using (ISessionTransaction sameInvocation = new SessionStore().Open(sameInvocationSession))
        {
            Assert.Empty(sameInvocation.Shards);
            Assert.Equal(1, sameInvocation.ConflictedShardCount);
        }

        var jsonOut = new StringWriter();
        int verifyCode = new Cli().Run(
            ["verify", firstPath, conflictingPath, "--session", sessionPath, "--json"],
            jsonOut, new StringWriter());
        Assert.Equal(3, verifyCode);
        using (JsonDocument report = JsonDocument.Parse(jsonOut.ToString()))
            Assert.Equal(1, report.RootElement.GetProperty("terminalConflicts").GetInt32());

        // Across watch-style calls, the first returned batch owns its payload until the caller
        // applies the later marker. The second call must not credit those bytes early.
        var crossBatch = new ShardDecoder.SuccessfulShardRetentionBudget(1);
        List<DecodedShard> retainedFirstBatch = decoder.CollectShards([firstPath], _ => { }, crossBatch);
        long beforeConflict = crossBatch.RetainedBytes;
        DecodedShard crossBatchMarker = Assert.Single(decoder.CollectShards(
            [conflictingPath], _ => { }, crossBatch));
        Assert.True(crossBatchMarker.IsTerminalConflict);
        Assert.Equal(beforeConflict, crossBatch.RetainedBytes);
        int wouldOnlyFitAfterFalseCredit = checked((int)(1_000_000 - beforeConflict + 1));
        Assert.Equal(SuccessfulShardAdmissionKind.Refused, crossBatch.TryAdmit(
            Header([3], whole, index: 1, flags: 0), [3], wouldOnlyFitAfterFalseCredit).Kind);
        GC.KeepAlive(retainedFirstBatch);

        crossBatch.ReleasePersistedConflicts([crossBatchMarker]);
        Assert.Equal(SuccessfulShardAdmissionKind.Added, crossBatch.TryAdmit(
            Header([3], whole, index: 1, flags: 0), [3], wouldOnlyFitAfterFalseCredit).Kind);
    }

    [Fact]
    public void CrcValidFamilyMismatch_IsFatalBeforeConflictCanonicalizationOrSessionMutation()
    {
        using var tmp = new TempDir();
        Layout layout = Layout.Create(900, 900, 3, 4, 32);
        byte[] whole = TestData.Random(64, seed: 74);
        byte[] firstPayload = TestData.Random(64, seed: 75);
        byte[] conflictingPayload = TestData.Random(64, seed: 76);
        ShardHeader firstHeader = Header(firstPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1, fileName: "original.bin");
        ShardHeader conflictingHeader = Header(conflictingPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1, fileName: "original.bin");
        ShardHeader changedNameHeader = Header(firstPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1, fileName: "renamed.bin");
        string firstPath = tmp.File("family-a.png");
        string conflictingPath = tmp.File("family-b.png");
        string changedNamePath = tmp.File("family-z.png");
        Render(firstHeader, firstPayload, layout, firstPath);
        Render(conflictingHeader, conflictingPayload, layout, conflictingPath);
        Render(changedNameHeader, firstPayload, layout, changedNamePath);

        string settingsPath = tmp.File("parallel-family-settings.json");
        File.WriteAllText(settingsPath,
            """{ "DecodeMaxParallelism": 2, "DecodeMemoryBudgetMB": 64 }""");
        AppSettings settings = AppSettings.Load(settingsPath);
        var decoder = new ShardDecoder(settings, new CameraRectifier(),
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());

        // Exercise the parallel fresh-batch path: scheduling may choose either family first, but
        // the run-wide FileId family check must reject the pair in either order.
        Assert.Throws<ShardFamilyMismatchException>(() =>
            decoder.CollectShards([firstPath, changedNamePath], _ => { }));

        // Once A/B made an ordinal terminal, a later C with poisoned family metadata must still
        // be rejected rather than disappearing as another terminal-conflict duplicate.
        var shared = new ShardDecoder.SuccessfulShardRetentionBudget(64);
        DecodedShard marker = Assert.Single(decoder.CollectShards(
            [firstPath, conflictingPath], _ => { }, shared));
        Assert.True(marker.IsTerminalConflict);
        Assert.Throws<ShardFamilyMismatchException>(() =>
            decoder.CollectShards([changedNamePath], _ => { }, shared));

        var verifyOut = new StringWriter();
        var verifyErr = new StringWriter();
        int verifyCode = new Cli(settings).Run(
            ["verify", firstPath, changedNamePath, "--json"], verifyOut, verifyErr);
        Assert.Equal(1, verifyCode);
        Assert.Contains("inconsistent", verifyErr.ToString(), StringComparison.OrdinalIgnoreCase);

        string sessionPath = tmp.File("family-mismatch.qrsession");
        var decodeOut = new StringWriter();
        var decodeErr = new StringWriter();
        int decodeCode = new Cli(settings).Run(
            ["decode", firstPath, changedNamePath, "--session", sessionPath, "--json"],
            decodeOut, decodeErr);
        Assert.Equal(1, decodeCode);
        Assert.Contains("inconsistent", decodeErr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(sessionPath));
        using ISessionTransaction transaction = new SessionStore().Open(sessionPath);
        Assert.Empty(transaction.Shards);
        Assert.Equal(0, transaction.ConflictedShardCount);
    }

    [Fact]
    public void FusionConflict_CannotReusePayloadStillOwnedByMaterializedShardList()
    {
        using var tmp = new TempDir();
        Layout layout = Layout.Create(900, 900, 3, 4, 32);
        byte[] whole = TestData.Random(64, seed: 81);
        byte[] firstPayload = TestData.Random(64, seed: 82);
        byte[] conflictingPayload = TestData.Random(64, seed: 83);
        ShardHeader firstHeader = Header(firstPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1);
        string firstPath = tmp.File("a.png");
        string failed1 = tmp.File("y.png");
        string failed2 = tmp.File("z.png");
        Render(firstHeader, firstPayload, layout, firstPath);
        // Valid ECC/header but a deliberately mismatched payload CRC produces fusion salvage.
        Render(firstHeader, conflictingPayload, layout, failed1);
        Render(firstHeader, conflictingPayload, layout, failed2);

        var firstShard = new DecodedShard(firstHeader, firstPayload, firstPath, layout.EccParity, 0);
        int firstCharge = ShardDecoder.SuccessfulShardRetentionBudget.RetentionCharge(firstShard);
        const string fusedSource = "fused";
        int uniqueOverhead = 2 * ShardHeader.Size("conflict.bin") + 2 * fusedSource.Length +
            ShardDecoder.SuccessfulShardRetentionBudget.PerShardOverheadBytes;
        int uniquePayloadLength = 1_000_000 - firstCharge + 1 - uniqueOverhead;
        Assert.True(uniquePayloadLength > 0);
        byte[] uniquePayload = new byte[uniquePayloadLength];
        var conflict = Shard(conflictingPayload, whole, 0, 0,
            count: 2, stripeData: 2, stripeParity: 1, source: "fused-conflict");
        var unique = Shard(uniquePayload, whole, 1, 0,
            count: 2, stripeData: 2, stripeParity: 1, source: fusedSource);
        var decoder = new ShardDecoder(AppSettings.BuiltIn, new CameraRectifier(),
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(),
            new ScriptedFusion([conflict, unique]), new Interleaver2());
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(1);

        Assert.Throws<ShardResourceLimitException>(() =>
            decoder.CollectShards([firstPath, failed1, failed2], _ => { }, successful));
        Assert.Equal(firstCharge, successful.RetainedBytes);
    }

    [Fact]
    public void PhotoFusion_CapsCaptureCountAndAvoidsQuadraticMajorityWork()
    {
        Layout layout = FusionLayout();
        byte[] cells = new byte[layout.CodewordCount * Fec.CodewordLength];
        var failures = Enumerable.Range(0, 10_000)
            .Select(i => new FailedCapture(layout, cells, $"capture-{i}"))
            .ToList();
        var log = new List<string>();

        long before = GC.GetAllocatedBytesForCurrentThread();
        new PhotoFusion().Fuse(failures, log.Add);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 5_000_000, $"bounded fusion allocated {allocated:N0} bytes");
        Assert.Contains(log, line => line.Contains("9,992") && line.Contains("bounded fusion"));
    }

    [Fact]
    public void PhotoFusion_RefusesPathologicalTwoCaptureHypothesisWork()
    {
        const int parity = 16;
        const int clusters = 6;
        long attempts = (1L << clusters) - 2;
        int largestAllowed = (int)(PhotoFusion.MaxClusterHypothesisWork / attempts / (parity + 2L));

        Assert.True(PhotoFusion.IsClusterHypothesisWorkAllowed(largestAllowed, clusters, parity));
        Assert.False(PhotoFusion.IsClusterHypothesisWorkAllowed(largestAllowed + 1, clusters, parity));
        Assert.False(PhotoFusion.IsClusterHypothesisWorkAllowed(100, clusters: 1, parity: parity));
        Assert.False(PhotoFusion.IsClusterHypothesisWorkAllowed(100, clusters, parity: int.MaxValue));

        long runWideRemaining = PhotoFusion.MaxClusterHypothesisWork;
        Assert.True(PhotoFusion.TryReserveClusterHypothesisWork(
            largestAllowed, clusters, parity, ref runWideRemaining));
        Assert.False(PhotoFusion.TryReserveClusterHypothesisWork(
            largestAllowed, clusters, parity, ref runWideRemaining));
    }

    [Fact]
    public void PhotoFusion_ContinuesPastRsValidMaskWhosePayloadCrcFails()
    {
        const int parity = 16;
        Layout layout = FusionLayout(); // exactly one 255-byte codeword
        byte[] payload = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();
        ShardHeader header = Header(payload, payload, index: 0, flags: 0,
            count: 1, stripeData: 0, stripeParity: 0, fileName: "fusion.bin");
        byte[] headerBytes = header.Serialize();
        var validStream = new byte[Fec.DataLength(parity)];
        headerBytes.CopyTo(validStream, 0);
        payload.CopyTo(validStream, headerBytes.Length);

        // This is a second perfectly RS-valid codeword, but its embedded payload bytes no longer
        // match the unchanged header CRC. Its data difference and parity difference form two
        // disconnected spatial clusters in this one-codeword layout.
        byte[] invalidStream = (byte[])validStream.Clone();
        for (int i = 0; i < 12; i++)
            invalidStream[headerBytes.Length + 26 + i] ^= 0x5A;
        var fec = new Fec();
        byte[] validCodeword = fec.Protect(validStream, parity, cwCount: 1);
        byte[] invalidCodeword = fec.Protect(invalidStream, parity, cwCount: 1);

        var captureA = new byte[Fec.CodewordLength];
        var captureB = new byte[Fec.CodewordLength];
        for (int i = 0; i < Fec.CodewordLength; i++)
        {
            bool dataRegion = i < Fec.DataLength(parity);
            captureA[i] = dataRegion ? validCodeword[i] : invalidCodeword[i];
            captureB[i] = dataRegion ? invalidCodeword[i] : validCodeword[i];
        }
        Assert.False(fec.TryRecover(captureA, parity, 1, out _, out _));
        Assert.False(fec.TryRecover(captureB, parity, 1, out _, out _));

        List<DecodedShard> fused = new PhotoFusion().Fuse(
            [new FailedCapture(layout, captureA, "a.png"), new FailedCapture(layout, captureB, "b.png")],
            _ => { });

        DecodedShard restored = Assert.Single(fused);
        Assert.Equal(payload, restored.Payload);
    }

    [Fact]
    public void PortableArchiveCanonicalization_CollapsesCaseAndUnicodeAliases()
    {
        Assert.True(ShardAssembler.TryCanonicalizePortableArchiveSegment("ReadMe", out _, out string mixed));
        Assert.True(ShardAssembler.TryCanonicalizePortableArchiveSegment("readme", out _, out string lower));
        Assert.Equal(mixed, lower);

        Assert.True(ShardAssembler.TryCanonicalizePortableArchiveSegment("\u00e9", out string composed, out string composedKey));
        Assert.True(ShardAssembler.TryCanonicalizePortableArchiveSegment("e\u0301", out string decomposed, out string decomposedKey));
        Assert.Equal(composed, decomposed);
        Assert.Equal(composedKey, decomposedKey);
    }

    [Fact]
    public void PortableArchiveCanonicalization_InvariantPolicyRejectsOnlyNonAscii()
    {
        Assert.True(ShardAssembler.TryCanonicalizePortableArchiveSegment(
            "File.TXT", unicodeCanonicalizationAvailable: false, out _, out string asciiKey));
        Assert.Equal("FILE.TXT", asciiKey);
        Assert.False(ShardAssembler.TryCanonicalizePortableArchiveSegment(
            "\u00e9.txt", unicodeCanonicalizationAvailable: false, out _, out _));
    }

    private sealed class CountingRectifier : ICameraRectifier
    {
        internal int TryRectifyCalls { get; private set; }

        public Bitmap? TryRectify(Bitmap photo)
        {
            TryRectifyCalls++;
            return null;
        }

        public CameraPose? DetectPose(Bitmap photo) => null;

        public Bitmap RectifyWithPose(Bitmap photo, CameraPose pose) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedFusion(List<DecodedShard> output) : IPhotoFusion
    {
        public List<DecodedShard> Fuse(IReadOnlyList<FailedCapture> failures, Action<string> log)
        {
            Assert.True(failures.Count >= 2);
            return output;
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
