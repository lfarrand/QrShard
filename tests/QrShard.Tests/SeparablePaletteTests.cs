using QrShard;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// The gradient sampling path classifies through <see cref="SeparablePalette"/> instead of
/// <see cref="Palette.Nearest"/>. Its output feeds erasure flagging and Chase trials, so anything
/// less than bit-identical agreement with the scan is silent corruption. Densities 2–8 are compared
/// exhaustively over the whole RGB cube. 9 and 10 stay on channel boundaries, ties, and a lattice
/// so the check does not scan every RGB value against a 512- or 1024-colour palette.
/// </summary>
public class SeparablePaletteTests
{
    /// <summary>Every one of the 2^24 RGB inputs, for densities whose palettes stay small enough to scan.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MatchesScan_ExhaustivelyOverEveryRgbInput(int bits)
    {
        var palette = new Palette().Build(bits);
        AssertMatchesScanOverWholeCube(palette, $"theoretical bits={bits}");
    }

    /// <summary>
    /// 9 and 10 bits keep the same product structure. Samples cover every palette colour, the
    /// integer midpoint of each channel gap (an exact tie when the gap is even), the samples on
    /// either side of that midpoint, and a step-16 lattice.
    /// </summary>
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void MatchesScan_OnChannelBoundariesTiesAndALattice(int bits)
    {
        var palette = new Palette().Build(bits);
        var index = new SeparablePalette();
        Assert.True(index.TryRebuild(palette), $"bits={bits}");
        var scan = new Palette();
        var seen = new HashSet<int>();

        void Check(int r, int g, int b)
        {
            r = Math.Clamp(r, 0, 255);
            g = Math.Clamp(g, 0, 255);
            b = Math.Clamp(b, 0, 255);
            if (!seen.Add((r << 16) | (g << 8) | b))
                return;
            int fast = index.Nearest(r, g, b);
            int slow = scan.Nearest(palette, r, g, b);
            Assert.Equal(slow, fast);
        }

        foreach (Rgb24 colour in palette)
            Check(colour.R, colour.G, colour.B);

        byte[] levelsR = palette.Select(c => c.R).Distinct().OrderBy(v => v).ToArray();
        byte[] levelsG = palette.Select(c => c.G).Distinct().OrderBy(v => v).ToArray();
        byte[] levelsB = palette.Select(c => c.B).Distinct().OrderBy(v => v).ToArray();
        void Boundaries(byte[] moving, byte[] otherA, byte[] otherB, int channel)
        {
            for (int i = 0; i < moving.Length - 1; i++)
            {
                int mid = (moving[i] + moving[i + 1]) / 2;
                foreach (byte a in otherA)
                {
                    foreach (byte b in otherB)
                    {
                        for (int d = -1; d <= 1; d++)
                        {
                            int v = mid + d;
                            if (channel == 0)
                                Check(v, a, b);
                            else if (channel == 1)
                                Check(a, v, b);
                            else
                                Check(a, b, v);
                        }
                    }
                }
            }
        }

        Boundaries(levelsR, levelsG, levelsB, 0);
        Boundaries(levelsG, levelsR, levelsB, 1);
        Boundaries(levelsB, levelsR, levelsG, 2);

        for (int r = 0; r < 256; r += 16)
            for (int g = 0; g < 256; g += 16)
                for (int b = 0; b < 256; b += 16)
                    Check(r, g, b);
    }

    /// <summary>
    /// The 1-bit palette is black/white, not a product of the generic channel split, so the fast
    /// path must decline it and leave the caller scanning.
    /// </summary>
    [Fact]
    public void OneBitPalette_IsDeclined() =>
        Assert.False(new SeparablePalette().TryRebuild(new Palette().Build(1)));

    /// <summary>
    /// What the gradient path actually classifies against: a per-channel gain applied to the
    /// theoretical palette (illumination), then the per-row lerp between two such strips.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void MatchesScan_ForGainedAndInterpolatedPalettes(int bits)
    {
        var theoretical = new Palette().Build(bits);
        var top = Gain(theoretical, 1.00, 0.94, 0.88);
        var bottom = Gain(theoretical, 0.71, 0.79, 0.65);

        AssertMatchesScanOverWholeCube(top, $"gained top bits={bits}");
        AssertMatchesScanOverWholeCube(bottom, $"gained bottom bits={bits}");

        // Sample the row lerp at both ends and across the middle, as GridSampler does per row.
        foreach (double t in new[] { 0.0, 0.17, 0.5, 0.83, 1.0 })
        {
            var row = new Rgb24[theoretical.Length];
            for (int i = 0; i < row.Length; i++)
                row[i] = new Rgb24(
                    (byte)(top[i].R + (bottom[i].R - top[i].R) * t + 0.5),
                    (byte)(top[i].G + (bottom[i].G - top[i].G) * t + 0.5),
                    (byte)(top[i].B + (bottom[i].B - top[i].B) * t + 0.5));
            AssertMatchesScanOverWholeCube(row, $"row lerp bits={bits} t={t}");
        }
    }

    /// <summary>
    /// Degenerate products the structure check accepts but which stress the tie-break: duplicated
    /// levels (two indices at the same color) and levels out of ascending order. The scan returns
    /// the lowest tying index; the tables must agree.
    /// </summary>
    [Fact]
    public void MatchesScan_ForDuplicateAndUnorderedLevels()
    {
        // bits=6 => 4 R levels, 4 G levels, 4 B levels.
        AssertMatchesScanOverWholeCube(Product([0, 90, 90, 200], [255, 10, 128, 60], [7, 7, 7, 7]),
            "duplicate + unordered levels");
        AssertMatchesScanOverWholeCube(Product([40, 40, 40, 40], [40, 40, 40, 40], [40, 40, 40, 40]),
            "every entry identical");
    }

    /// <summary>
    /// A palette knocked off the product grid — one entry moved by a single level in one channel,
    /// which is all capture noise on a calibration strip needs to do. Accepting it would make the
    /// tables disagree with the scan, so it must be declined.
    /// </summary>
    [Fact]
    public void NonProductPalette_IsDeclined()
    {
        for (int bits = 2; bits <= Palette.MaxBits; bits++)
        {
            var palette = new Palette().Build(bits);
            for (int i = 0; i < palette.Length; i++)
            {
                var damaged = (Rgb24[])palette.Clone();
                damaged[i] = new Rgb24((byte)(damaged[i].R ^ 1), damaged[i].G, damaged[i].B);
                Assert.False(new SeparablePalette().TryRebuild(damaged),
                    $"bits={bits}: entry {i} off the grid must be declined");
            }
        }
    }

    [Fact]
    public void MalformedLengths_AreDeclined()
    {
        Assert.False(new SeparablePalette().TryRebuild([]));
        Assert.False(new SeparablePalette().TryRebuild([new Rgb24(1, 2, 3), new Rgb24(4, 5, 6), new Rgb24(7, 8, 9)]));
        // 9 and 10 bits are in range. An all-zero palette is still a Cartesian product, so it is accepted.
        Assert.True(new SeparablePalette().TryRebuild(new Rgb24[1 << 9]));
        Assert.True(new SeparablePalette().TryRebuild(new Rgb24[1 << 10]));
        Assert.False(new SeparablePalette().TryRebuild(new Rgb24[1 << 11]));
        Assert.False(new SeparablePalette().TryRebuild(new Rgb24[1 << 16]));
    }

    /// <summary>
    /// Rebuild must fully replace the previous tables: a declined rebuild leaves stale state, so
    /// callers gate on the return value, and a successful one must not inherit anything.
    /// </summary>
    [Fact]
    public void Rebuild_ReplacesPreviousTables()
    {
        var index = new SeparablePalette();
        Assert.True(index.TryRebuild(new Palette().Build(8)));
        var six = new Palette().Build(6);
        Assert.True(index.TryRebuild(six));
        AssertMatchesScan(index, six, "rebuilt 8 -> 6");
    }

    private static void AssertMatchesScanOverWholeCube(Rgb24[] palette, string label)
    {
        var index = new SeparablePalette();
        Assert.True(index.TryRebuild(palette), $"{label}: expected a product palette");
        AssertMatchesScan(index, palette, label);
    }

    private static void AssertMatchesScan(SeparablePalette index, Rgb24[] palette, string label)
    {
        var scan = new Palette();
        // Parallel over the red axis; the whole cube is 16.7M points per palette.
        Parallel.For(0, 256, r =>
        {
            for (int g = 0; g < 256; g++)
            {
                for (int b = 0; b < 256; b++)
                {
                    int fast = index.Nearest(r, g, b);
                    int slow = scan.Nearest(palette, r, g, b);
                    if (fast != slow)
                        Assert.Fail($"{label}: rgb({r},{g},{b}) scan={slow} tables={fast}");
                }
            }
        });
    }

    private static Rgb24[] Gain(Rgb24[] palette, double gr, double gg, double gb)
    {
        var scaled = new Rgb24[palette.Length];
        for (int i = 0; i < palette.Length; i++)
            scaled[i] = new Rgb24(
                (byte)Math.Clamp(palette[i].R * gr, 0, 255),
                (byte)Math.Clamp(palette[i].G * gg, 0, 255),
                (byte)Math.Clamp(palette[i].B * gb, 0, 255));
        return scaled;
    }

    /// <summary>Builds the 64-entry product palette with the given per-channel levels.</summary>
    private static Rgb24[] Product(byte[] rLevels, byte[] gLevels, byte[] bLevels)
    {
        var palette = new Rgb24[rLevels.Length * gLevels.Length * bLevels.Length];
        for (int i = 0; i < palette.Length; i++)
            palette[i] = new Rgb24(
                rLevels[i / (gLevels.Length * bLevels.Length)],
                gLevels[i / bLevels.Length % gLevels.Length],
                bLevels[i % bLevels.Length]);
        return palette;
    }
}
