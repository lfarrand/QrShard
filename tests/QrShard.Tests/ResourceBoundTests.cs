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
    public void PermutationCache_StopsGrowingButKeepsReturningCorrectPermutations()
    {
        // The cache is keyed on a length derived from declared geometry and never evicted, and the
        // instance outlives any one decode. Past the cap it must degrade to uncached — still
        // correct, just recomputed — rather than grow without bound.
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
