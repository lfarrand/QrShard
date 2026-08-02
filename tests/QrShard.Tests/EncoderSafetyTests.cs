using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using QrShard;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>Protocol arithmetic, source stability, and all-or-nothing output publication.</summary>
public class EncoderSafetyTests
{
    private static readonly EncodeOptions Sparse = new()
    {
        Width = 700, Height = 700, CellPx = 20, BitsPerCell = 1, EccParity = 0, Compress = false,
    };

    private static readonly EncodeOptions Fast = new()
    {
        Width = 700, Height = 700, CellPx = 4, BitsPerCell = 4, EccParity = 8, Compress = false,
    };

    [Fact]
    public void Plan_AcceptsFiveMillionDataImages_AndRejectsTheNextOne()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("x.bin", [1]);
        var source = new SyntheticPayloadPreparer(1);
        var encoder = Encoder(source);
        long capacity = encoder.Plan(input, Sparse).BytesPerImage;

        source.Length = checked(capacity * ShardHeader.MaxImages);
        EncodePlan boundary = encoder.Plan(input, Sparse);
        Assert.Equal(ShardHeader.MaxImages, boundary.DataImages);
        Assert.Equal(ShardHeader.MaxImages, boundary.ImageCount);

        source.Length++;
        var ex = Assert.Throws<InvalidOperationException>(() => encoder.Plan(input, Sparse));
        Assert.Contains(ShardHeader.MaxImages.ToString("N0"), ex.Message);
    }

    [Fact]
    public void Plan_MaximumFountainGeometry_UsesCheckedLongArithmetic()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("x.bin", [1]);
        var source = new SyntheticPayloadPreparer(1);
        var encoder = Encoder(source);
        long capacity = encoder.Plan(input, Sparse).BytesPerImage;
        source.Length = checked(capacity * ShardHeader.MaxImages);

        EncodePlan plan = encoder.Plan(input, Sparse with { FountainPercent = ShardEncoder.MaxFountainPercent });

        Assert.Equal(ShardHeader.MaxImages, plan.DataImages);
        Assert.Equal(FountainFec.MaxStripeData, plan.StripeData);
        Assert.Equal(FountainFec.MaxStripeData * ShardEncoder.MaxFountainPercent / 100, plan.StripeParity);
        Assert.Equal(55_000_000, plan.ImageCount);
        Assert.All(new long[] { plan.ImageCount, plan.DataImages, plan.ParityImages, plan.BytesPerImage },
            value => Assert.True(value > 0));
    }

    [Fact]
    public void Plan_RejectsPlannerOverflowBeforeAnyArrayAllocation()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("x.bin", [1]);
        var encoder = Encoder(new SyntheticPayloadPreparer(1), new ExtremeStripePlanner());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            encoder.Plan(input, Sparse with { FountainPercent = 1 }));

        Assert.Contains("recovery images", ex.Message);
    }

    [Fact]
    public void PreparedAndEncryptedLengths_EnforceTheTopBoundaryWithoutAllocatingIt()
    {
        ShardEncoder.EnsurePreparedLengthSupported(ShardEncoder.MaxFileBytes);
        Assert.Throws<InvalidOperationException>(() =>
            ShardEncoder.EnsurePreparedLengthSupported(ShardEncoder.MaxFileBytes + 1));

        long largestPlaintext = ShardEncoder.MaxFileBytes - PayloadCipher.Overhead;
        PayloadPreparer.EnsureEncryptedPayloadFitsProtocol(largestPlaintext);
        Assert.Throws<InvalidOperationException>(() =>
            PayloadPreparer.EnsureEncryptedPayloadFitsProtocol(largestPlaintext + 1));
    }

    [Fact]
    public void ManagedEncryptionAndOptionalCompressionRejectImpossibleArrayLengthsBeforeAllocation()
    {
        long largestManagedPlaintext = (long)Array.MaxLength - PayloadCipher.Overhead;
        PayloadPreparer.EnsureEncryptedPayloadFitsManagedArray(largestManagedPlaintext);

        var encryption = Assert.Throws<InvalidOperationException>(() =>
            PayloadPreparer.EnsureEncryptedPayloadFitsManagedArray(largestManagedPlaintext + 1));
        Assert.Contains("contiguous managed payload", encryption.Message);

        Assert.True(PayloadPreparer.CompressionMaterializationFitsBudget(Array.MaxLength, 1_000_000));
        Assert.False(PayloadPreparer.CompressionMaterializationFitsBudget(
            (long)Array.MaxLength + 1, 1_000_000));
        Assert.Throws<InvalidOperationException>(() =>
            PayloadCipher.AllocateBlob(largestManagedPlaintext + 1));
    }

    [Fact]
    public void PayloadCipher_ClearsEveryDerivedKey_OnSuccessAndAuthenticationFailure()
    {
        int cleared = 0;
        var cipher = new PayloadCipher(key =>
        {
            Assert.All(key, value => Assert.Equal(0, value));
            Interlocked.Increment(ref cleared);
        });
        byte[] plaintext = TestData.Random(1_000);
        byte[] aad = PayloadCipher.BuildAad(plaintext.Length, SHA256.HashData(plaintext), "x.bin");

        byte[] blob = cipher.Encrypt(plaintext, "right", aad);
        ArraySegment<byte> restored = cipher.DecryptInPlace(blob, "right", "x.bin", aad);
        Assert.Equal(plaintext, restored.ToArray());

        byte[] wrongPasswordBlob = cipher.Encrypt(plaintext, "right", aad);
        Assert.Throws<ShardDecodeException>(() =>
            cipher.DecryptInPlace(wrongPasswordBlob, "wrong", "x.bin", aad));
        Assert.Equal(4, cleared); // two encryption KDFs plus successful and failed decryption KDFs
    }

    [Fact]
    public void PayloadCipher_ClearsPlaintextBlobWhenSealingFails()
    {
        byte[] malformed = Enumerable.Repeat((byte)0xA5, PayloadCipher.Overhead - 1).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PayloadCipher().SealInPlace(malformed, "secret"));

        Assert.All(malformed, value => Assert.Equal(0, value));
    }

    [Fact]
    public void PasswordCompressionClearsEveryTemporaryPlaintextBuffer()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("secret.txt", Enumerable.Repeat((byte)'A', 100_000).ToArray());
        int cleared = 0;
        var preparer = new PayloadPreparer(new PayloadCipher(), buffer =>
        {
            Assert.All(buffer, value => Assert.Equal(0, value));
            Interlocked.Increment(ref cleared);
        });

        using PayloadHandle payload = preparer.Open(input, new FileInfo(input).Length,
            compress: true, password: "secret", AppSettings.BuiltIn, semanticFlags: 0,
            out byte flags, out _);

        Assert.True((flags & ShardHeader.FlagCompressed) != 0);
        Assert.True((flags & ShardHeader.FlagEncrypted) != 0);
        Assert.True(cleared >= 3, $"Only {cleared} plaintext temporary buffer(s) were cleared.");
    }

    [Fact]
    public void Encode_RefusesANonEmptyDestination_WithoutChangingIt()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        string output = tmp.Sub("shards");
        string sentinel = Path.Combine(output, "keep.txt");
        File.WriteAllText(sentinel, "valuable");

        var ex = Assert.Throws<IOException>(() => new ShardEncoder().Encode(input, output, Fast));

        Assert.Contains("not empty", ex.Message);
        Assert.Equal("valuable", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(output));
        AssertNoStagingSibling(tmp.Path);
    }

    [Fact]
    public void Encode_PublishesCompleteGenerationAndReturnsFinalPaths()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(30_000));
        string output = tmp.Sub("shards");

        EncodeResult result = new ShardEncoder().Encode(input, output, Fast);

        string fullOutput = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
        Assert.All(result.Files, path =>
        {
            Assert.StartsWith(fullOutput, path);
            Assert.True(File.Exists(path));
        });
        Assert.Equal(result.ImageCount, Directory.EnumerateFiles(output).Count());
        AssertNoStagingSibling(tmp.Path);
    }

    [Fact]
    public void Encode_SecondGenerationCannotMixWithTheFirst()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(30_000));
        string output = tmp.Sub("shards");
        EncodeResult first = new ShardEncoder().Encode(input, output, Fast);
        var before = first.Files.ToDictionary(path => Path.GetFileName(path)!, path => File.ReadAllBytes(path));

        File.WriteAllBytes(input, TestData.Random(1_000, 99));
        Assert.Throws<IOException>(() => new ShardEncoder().Encode(input, output, Fast));

        Assert.Equal(before.Count, Directory.EnumerateFiles(output).Count());
        foreach ((string name, byte[] bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, name)));
        AssertNoStagingSibling(tmp.Path);
    }

    [Fact]
    public void RendererFailure_LeavesNoPartialGeneration()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(1_000));
        string output = tmp.Sub("shards");
        var encoder = Encoder(new PayloadPreparer(), renderer: new ThrowingRenderer());

        Assert.ThrowsAny<Exception>(() => encoder.Encode(input, output, Fast));

        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        AssertNoStagingSibling(tmp.Path);
    }

    [Fact]
    public void Encode_AcquiresOneInterleavePermutationForEveryWorker()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", TestData.Random(250_000));
        string output = tmp.File("shards");
        var renderer = new RecordingRenderer();
        var encoder = Encoder(new PayloadPreparer(), renderer: renderer);

        EncodeResult result = encoder.Encode(input, output, Fast with { Interleave2 = true });

        Assert.True(result.ImageCount > 1);
        Assert.Equal(1, renderer.PrepareCalls);
        Assert.Equal(result.ImageCount, renderer.Seen.Count);
        Assert.All(renderer.Seen, permutation => Assert.Same(renderer.Permutation, permutation));
        Assert.All(result.Files, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task InputMutationDuringEncode_FailsBeforePublication()
    {
        using var tmp = new TempDir();
        string input = tmp.WriteFile("input.bin", Enumerable.Repeat((byte)'A', 50_000).ToArray());
        string output = tmp.File("shards");
        using var renderer = new BlockingRenderer();
        var encoder = Encoder(new PayloadPreparer(), renderer: renderer);

        // Encode is synchronous and enters a Parallel.For render stage. Keep its caller off the
        // constrained test-runner pool, then await (rather than synchronously block on) entry into
        // the injected renderer so the render workers always have a chance to run.
        Task<EncodeResult> encode = Task.Factory.StartNew(
            () => encoder.Encode(input, output, Fast with { Compress = true }),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Exception? orchestrationFailure = null;
        try
        {
            Task entered = renderer.Entered.Task;
            Task first = await Task.WhenAny(entered, encode, Task.Delay(TimeSpan.FromSeconds(10)));
            if (first == encode && !entered.IsCompletedSuccessfully)
            {
                _ = await encode; // propagate an early encoder failure instead of masking it as a timeout
                Assert.Fail("encode completed before the renderer entered");
            }
            Assert.True(entered.IsCompletedSuccessfully, "renderer did not enter");
            File.WriteAllBytes(input, Enumerable.Repeat((byte)'B', 50_000).ToArray());
        }
        catch (Exception orchestrationException)
        {
            orchestrationFailure = orchestrationException;
        }
        finally
        {
            renderer.Release.Set();
        }

        // WaitAsync does not stop the underlying task. Always observe and bounded-join it after
        // releasing the renderer so a failed handshake cannot race renderer/TempDir disposal.
        Exception? encodeFailure = await Record.ExceptionAsync(async () =>
            await encode.WaitAsync(TimeSpan.FromSeconds(10)));
        if (orchestrationFailure is not null)
            ExceptionDispatchInfo.Capture(orchestrationFailure).Throw();

        var ex = Assert.IsType<IOException>(encodeFailure);
        Assert.Contains("changed", ex.Message);
        Assert.False(Directory.Exists(output));
        AssertNoStagingSibling(tmp.Path);
    }

    private static ShardEncoder Encoder(IPayloadPreparer preparer, IStripePlanner? planner = null,
        IShardRenderer? renderer = null) =>
        new(AppSettings.BuiltIn, preparer, planner ?? new StripePlanner(), renderer ?? new ShardRenderer(),
            new CrossShardFec(), new FountainFec(), new Crc(), new Palette(), new ShardImageFormat());

    private static void AssertNoStagingSibling(string parent) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent, ".qrshard-encode-*.tmp"));

    private sealed class SyntheticPayloadPreparer(long length) : IPayloadPreparer
    {
        public long Length { get; set; } = length;

        public PayloadHandle Open(string filePath, long originalLength, bool compress, string? password,
            AppSettings cfg, byte semanticFlags, out byte flags, out byte[] sha)
        {
            flags = semanticFlags;
            sha = SHA256.HashData(File.ReadAllBytes(filePath));
            return new PayloadHandle(new SyntheticPayloadSource(Length));
        }

        public bool LooksCompressible(IPayloadSource source) => false;
    }

    private sealed class SyntheticPayloadSource(long length) : IPayloadSource
    {
        public long Length { get; } = length;
        public long ResidentBytes => 0;
        public void Read(long offset, Span<byte> destination) =>
            throw new InvalidOperationException("A dry-run must not read the synthetic payload.");
        public void Dispose() { }
    }

    private sealed class ExtremeStripePlanner : IStripePlanner
    {
        public (int StripeData, int StripeParity) PlanStripes(int count, int recoveryPercent) =>
            (1, int.MaxValue);

        public (int StripeData, int CodedPerStripe) PlanFountain(int count, int fountainPercent) =>
            (1, int.MaxValue);
    }

    private sealed class ThrowingRenderer : IShardRenderer
    {
        private readonly ShardRenderer inner = new();

        public ShardImageWriter CreateWriter(string format, Layout layout, AppSettings cfg) =>
            inner.CreateWriter(format, layout, cfg);

        public int[]? PrepareInterleave(Layout layout) => null;

        public void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream,
            int streamLength, string outPath, RenderScratch scratch, ShardImageWriter writer,
            int[]? interleavePermutation = null)
        {
            File.WriteAllText(outPath, "partial");
            throw new InvalidOperationException("injected renderer failure");
        }
    }

    private sealed class BlockingRenderer : IShardRenderer, IDisposable
    {
        private readonly ShardRenderer inner = new();
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public ShardImageWriter CreateWriter(string format, Layout layout, AppSettings cfg) =>
            inner.CreateWriter(format, layout, cfg);

        public int[]? PrepareInterleave(Layout layout) => null;

        public void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream,
            int streamLength, string outPath, RenderScratch scratch, ShardImageWriter writer,
            int[]? interleavePermutation = null)
        {
            File.WriteAllText(outPath, "complete staged image");
            Entered.TrySetResult(true);
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("test did not release renderer");
        }

        public void Dispose()
        {
            Release.Dispose();
        }
    }

    private sealed class RecordingRenderer : IShardRenderer
    {
        private readonly ShardRenderer inner = new();
        private int prepareCalls;
        public int[] Permutation { get; } = [42];
        public int PrepareCalls => Volatile.Read(ref prepareCalls);
        public System.Collections.Concurrent.ConcurrentBag<int[]?> Seen { get; } = [];

        public ShardImageWriter CreateWriter(string format, Layout layout, AppSettings cfg) =>
            inner.CreateWriter(format, layout, cfg);

        public int[]? PrepareInterleave(Layout layout)
        {
            Interlocked.Increment(ref prepareCalls);
            return Permutation;
        }

        public void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream,
            int streamLength, string outPath, RenderScratch scratch, ShardImageWriter writer,
            int[]? interleavePermutation = null)
        {
            Seen.Add(interleavePermutation);
            File.WriteAllText(outPath, "complete staged image");
        }
    }
}
