using SixLabors.ImageSharp.PixelFormats;
using QrShard;

namespace QrShard.Tests;

/// <summary>
/// Allocation sizes derived from attacker-declared header fields. Every one of these was bounded
/// against what the ENCODER can produce, and none against the image actually in hand or against a
/// budget — so a small crafted input could ask for gigabytes. The theme is the same as the
/// decode-validation round: a limit that only describes legitimate output is not a limit.
/// </summary>
public class ResourceBoundTests
{
    [Fact]
    public void OrdinaryShardLoads_AreBoundToOneFrameAndSkipMetadata()
    {
        // The folder and incremental APIs accept one shard image per item. ImageSharp otherwise
        // loads every frame in an animated container before ShardDecoder copies only the root,
        // letting a many-frame file exceed the worker budget by orders of magnitude. Recording
        // decode has its own path and deliberately does not use these options.
        var options = ShardDecoder.NewShardImageDecoderOptions();
        Assert.Equal(1u, options.MaxFrames);
        Assert.True(options.SkipMetadata);
    }

    [Fact]
    public void AnimatedRecordingAndApngLimits_AreFiniteAndConsistent()
    {
        Assert.Equal(256L * 1024 * 1024, RecordingFrameSource.MaxAnimatedDecodedBytes);
        Assert.Equal(4096, RecordingFrameSource.MaxAnimatedFrames);
        Assert.Equal(RecordingFrameSource.MaxAnimatedDecodedBytes, SlideshowWriter.MaxApngDecodedBytes);
        Assert.Equal(1, RecordingFrameSource.AllowedAnimatedFrames(3840, 2160, budgetMB: 64));
        Assert.Equal(0, RecordingFrameSource.AllowedAnimatedFrames(8000, 6000, budgetMB: 4000));
    }

    [Fact]
    public void LiveWorkersAndTemporalAverageRespectDecodeBudget()
    {
        var phoneFrame = new Bitmap(new Rgb24[1], 8000, 6000); // dimensions only
        Assert.Equal(2, VideoDecoder.BudgetedLiveWorkers(phoneFrame, requestedWorkers: 64, budgetMB: 4000));
        Assert.True(VideoDecoder.CanTemporalAverage(phoneFrame, budgetMB: 4000));

        var larger = new Bitmap(new Rgb24[1], 10_000, 8_000);
        Assert.False(VideoDecoder.CanTemporalAverage(larger, budgetMB: 4000));
    }

    [Fact]
    public void InvalidImageDimensionsAreRejectedBeforeWorkerMath()
    {
        Assert.Throws<ShardDecodeException>(() =>
            ShardDecoder.ValidateImageDimensions(0, 100, budgetMB: 4000));
        Assert.Throws<ShardDecodeException>(() =>
            ShardDecoder.ValidateImageDimensions(int.MaxValue, int.MaxValue, budgetMB: 4000));
    }

    [Fact]
    public void CompressionAndEncryptionMaterializationAreBudgetCheckedBeforeAllocation()
    {
        Assert.True(PayloadPreparer.CompressionMaterializationFitsBudget(100_000_000, budgetMB: 400));
        Assert.False(PayloadPreparer.CompressionMaterializationFitsBudget(100_000_000, budgetMB: 399));
        PayloadPreparer.EnsureEncryptionFitsBudget(63_000_000, budgetMB: 64);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PayloadPreparer.EnsureEncryptionFitsBudget(65_000_000, budgetMB: 64));
        Assert.Contains("EncodeMemoryBudgetMB", ex.Message);
    }

    [Fact]
    public void EncodeWorkerBudgetCountsAllRetainedScratchAndWriterPixels()
    {
        var layout = Layout.Create(1920, 1080, 1, 8, 32, interleave2: true);
        const long stream = 123_456;
        long pixels = (long)layout.Width * layout.Height * 3;
        long expectedPng = pixels + stream + 2 * layout.TotalBytes;

        Assert.Equal(expectedPng,
            ShardEncoder.EstimateRenderWorkerBytes(layout, stream, imageWriterCopiesPixels: false));
        Assert.Equal(expectedPng + pixels,
            ShardEncoder.EstimateRenderWorkerBytes(layout, stream, imageWriterCopiesPixels: true));
    }

    [Fact]
    public void ArchiveEntryCountLimit_AcceptsTheBoundaryAndRejectsTheNextEntry()
    {
        ShardAssembler.EnsureArchiveEntryCount(ShardAssembler.MaxArchiveEntries);
        var ex = Assert.Throws<ShardDecodeException>(() =>
            ShardAssembler.EnsureArchiveEntryCount(ShardAssembler.MaxArchiveEntries + 1));
        Assert.Contains(ShardAssembler.MaxArchiveEntries.ToString("N0"), ex.Message);
    }

    [Fact]
    public void ArchivePathNodeLimit_AcceptsTheBoundaryAndRejectsTheNextNode()
    {
        ShardAssembler.EnsureArchivePathNodeCount(ShardAssembler.MaxArchivePathNodes);
        var ex = Assert.Throws<ShardDecodeException>(() =>
            ShardAssembler.EnsureArchivePathNodeCount(ShardAssembler.MaxArchivePathNodes + 1));
        Assert.Contains(ShardAssembler.MaxArchivePathNodes.ToString("N0"), ex.Message);
    }

    [Fact]
    public void ANewlyLoadedImageCannotExceedTheSingleWorkerPlanningBudget()
    {
        // The largest canvas QrShard can itself render remains decodable under the default.
        ShardDecoder.ValidateImageDimensions(16_384, 16_384, budgetMB: 4_000); // ~1.61 GB load peak
        var ex = Assert.Throws<ShardDecodeException>(() =>
            ShardDecoder.ValidateImageDimensions(20_000, 20_000, budgetMB: 2_000));
        Assert.Contains("DecodeMemoryBudgetMB", ex.Message);
        Assert.Contains("2,400 MB", ex.Message);
    }

    private static Layout DeclaredGrid(int gridW, int gridH) => new()
    {
        BitsPerCell = 8,
        CellPx = 1,
        GridW = gridW,
        GridH = gridH,
        MetaH = 1,
        InnerW = 2 * 1 + gridW,
        InnerH = 6 * 1 + gridH,
        EccParity = 0,
        FinderModule = 0,
    };

    [Fact]
    public void DeclaredGridFinerThanTheCapture_IsRejectedBeforeSizingBuffers()
    {
        // metaH = cellPx = 1 admits a 16382x16378 grid, ~268M cells, which slips just under the
        // cell-count ceiling and asks for hundreds of MB of scratch — PER WORKER, up to 24 of
        // them. Nothing tied the declared geometry to the bitmap: the scale factor just became
        // tiny and every sample coordinate clamped into range.
        var tiny = new Bitmap(new Rgb24[64 * 64], 64, 64);
        var inner = new InnerRect(0, 0, 64, 64);

        var ex = Record.Exception(() => new GridSampler().ReadDataGrid(
            tiny, inner, DeclaredGrid(16382, 16378), PaletteFor(8), new DecodeScratch(), out _, out _));

        Assert.IsType<ShardDecodeException>(ex);
        Assert.Contains("finer than", ex.Message);
    }

    [Fact]
    public void AGridThatFitsTheCapture_IsStillAccepted()
    {
        // The guard must reject geometry finer than the pixels available, not ordinary geometry.
        // A real capture clears it with room to spare, since inner.W is about InnerW >= GridW.
        var bmp = new Bitmap(new Rgb24[256 * 256], 256, 256);
        var inner = new InnerRect(0, 0, 256, 256);

        var ex = Record.Exception(() => new GridSampler().ReadDataGrid(
            bmp, inner, DeclaredGrid(200, 200), PaletteFor(8), new DecodeScratch(), out _, out _));

        Assert.Null(ex);
    }

    [Fact]
    public void ScratchBuffers_AreHandedBackWhenAMuchSmallerImageFollows()
    {
        // Buffers grew to the largest image a worker had ever seen and were never released, so one
        // oversized image raised that worker's floor for the whole run — times up to 24 workers.
        var scratch = new DecodeScratch();

        var big = scratch.Pixels(40_000_000);
        Assert.True(big.Length >= 40_000_000);

        // A 4K frame after it: two orders smaller, so the retained buffer is well past the 4x
        // hysteresis and should be replaced rather than kept.
        var small = scratch.Pixels(8_300_000);
        Assert.True(small.Length < big.Length,
            $"scratch kept {big.Length:N0} slots for an image needing {small.Length:N0}");

        // Within the hysteresis it must NOT churn: same array back, no reallocation.
        var again = scratch.Pixels(8_000_000);
        Assert.Same(small, again);
    }

    [Fact]
    public void PermutationCache_IsByteBoundedAndKeepsReturningCorrectPermutations()
    {
        // The cache is keyed on a length derived from declared geometry and never evicted, and the
        // instance outlives any one decode. It must retain one useful permutation rather than 64
        // arrays whose total size can exceed a gigabyte.
        var interleaver = new Interleaver2();

        for (int i = 0; i < 200; i++)
        {
            int length = 1000 + i;
            var perm = interleaver.Permutation(length);

            // A permutation of 0..length-1: every index present exactly once. If the cap turned
            // into a wrong-answer path rather than a slow one, this is what would catch it.
            Assert.Equal(length, perm.Length);
            var seen = new bool[length];
            foreach (int v in perm)
            {
                Assert.InRange(v, 0, length - 1);
                Assert.False(seen[v], $"index {v} appears twice for length {length}");
                seen[v] = true;
            }
        }

        // Determinism is the wire-format contract — both sides derive π from the length alone, so
        // a cached and an uncached result must be identical.
        Assert.Equal(interleaver.Permutation(1000), new Interleaver2().Permutation(1000));
        Assert.InRange(interleaver.CachedBytes, 0, 32 * 1024 * 1024);
    }

    [Fact]
    public void CameraBinarizer_RejectsAnImageTooLargeForItsSummedAreaTables()
    {
        // Two long[] tables over (w+1)*(h+1) is ~16 bytes per pixel — an order more than the
        // decode buffers beside it, and roughly 8 GB at the 500M-pixel per-image ceiling. It
        // surfaced as an uncaught OutOfMemoryException, which is deliberately fatal in
        // CollectShards and so took the whole batch down with it.
        //
        // The dimensions are what the guard reads, so a 1-pixel backing array is enough to prove
        // it refuses before touching the pixels.
        var oversized = new Bitmap(new Rgb24[1], 100_000, 1_000); // 100M pixels

        var ex = Record.Exception(() => new AdaptiveBinarizer().Threshold(oversized));

        Assert.IsType<ShardDecodeException>(ex);
        Assert.Contains("too large for camera-capture binarization", ex.Message);
    }

    private static PaletteSet PaletteFor(int bits)
    {
        var p = new Palette().Build(bits);
        return new PaletteSet(p, p, p, Interpolate: false);
    }
}
