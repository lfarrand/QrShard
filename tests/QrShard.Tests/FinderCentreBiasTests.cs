using SixLabors.ImageSharp.PixelFormats;

namespace QrShard.Tests;

/// <summary>
/// The finder detector's two axes disagreed about where a pixel is.
///
/// A run covering pixel indices a..b inclusive occupies [a, b+1) in the continuous coordinates
/// everything downstream uses, so its centre is (a + b + 1) / 2. The horizontal scan gets this
/// right — it works from run lengths, and l2/2 already carries the +1. The vertical scan works
/// from inclusive indices and computed (top + bottom) / 2, which is half a pixel short. The
/// inconsistency is visible within VerifyVertical itself: the run LENGTH on the line above is
/// (bottom - top + 1), with the +1 the centre then does without.
/// </summary>
public class FinderCentreBiasTests
{
    private const int Module = 10;
    private const int OriginX = 100;
    private const int OriginY = 200;
    private const int Width = 400;
    private const int Height = 500;

    /// <summary>
    /// Paints one 7x7-module finder: dark ring, light ring, 3x3 dark core. Any row or column
    /// through the middle reads 1:1:3:1:1, which is exactly what the detector looks for.
    /// </summary>
    private static bool[] FinderPattern()
    {
        var dark = new bool[Width * Height];
        for (int my = 0; my < 7; my++)
            for (int mx = 0; mx < 7; mx++)
            {
                int ring = Math.Min(Math.Min(mx, my), Math.Min(6 - mx, 6 - my));
                bool isDark = ring != 1; // ring 0 = outer dark, ring 1 = light, ring 2+ = dark core
                if (!isDark)
                    continue;
                for (int py = 0; py < Module; py++)
                    for (int px = 0; px < Module; px++)
                    {
                        int x = OriginX + mx * Module + px, y = OriginY + my * Module + py;
                        dark[y * Width + x] = true;
                    }
            }
        return dark;
    }

    [Fact]
    public void ADetectedFinderCentreIsUnbiasedInBothAxes()
    {
        var dark = FinderPattern();
        var photo = new Bitmap(new Rgb24[Width * Height], Width, Height);

        var clusters = new FinderDetector().FindCandidates(photo, dark);

        var found = Assert.Single(clusters);

        // The pattern spans [OriginX, OriginX + 7*Module) so its centre sits at 3.5 modules in.
        const double trueX = OriginX + 3.5 * Module;
        const double trueY = OriginY + 3.5 * Module;

        // Half a pixel of systematic error is a real slice of the sampling margin at the 1-3 px
        // cell sizes this tool encodes at, and it fed straight into Homography.Solve as a photo
        // corner, so every rectified canvas inherited it.
        Assert.Equal(trueX, found.X, precision: 6);
        Assert.Equal(trueY, found.Y, precision: 6);
    }

    [Fact]
    public void TheBiasIsSymmetricUnderTransposition()
    {
        // The strongest statement of the property: the detector must not care which axis it is
        // looking along. Transposing the image has to transpose the detected centre exactly, and
        // it did not — dy was short by half a pixel while dx was exact.
        var dark = FinderPattern();
        var transposed = new bool[Width * Height];
        int side = Math.Min(Width, Height);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                transposed[x * Width + y] = dark[y * Width + x];

        var photo = new Bitmap(new Rgb24[Width * Height], Width, Height);
        var normal = Assert.Single(new FinderDetector().FindCandidates(photo, dark));
        var flipped = Assert.Single(new FinderDetector().FindCandidates(photo, transposed));

        Assert.Equal(normal.X, flipped.Y, precision: 6);
        Assert.Equal(normal.Y, flipped.X, precision: 6);
    }
}
