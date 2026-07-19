using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Reads the self-describing metadata strip and the palette calibration strips.</summary>
internal sealed class StripReader(Palette palette) : IStripReader
{
    public StripReader() : this(new Palette())
    {
    }

    public Layout? ReadMetadata(Bitmap bmp, InnerRect inner)
    {
        // Before the metadata is read, gutter and strip height are only known via the shared
        // approximation innerWidth/100 (see Layout.EstimateMetaH). The strip is CRC-16
        // protected, so a small vertical search around the estimated line is free of false
        // positives and absorbs the residual geometric error of imperfect captures (rescaled
        // screenshots, camera rectification).
        double gutter = inner.W / 100.0;
        double metaH = Math.Max(6.0, inner.W / 100.0);

        // Offsets in strip-height fractions: estimated position first, then nearby lines.
        ReadOnlySpan<double> offsets = [0, -0.25, 0.25, -0.5, 0.5, -0.8];
        foreach (double offset in offsets)
        {
            // Redundant strips: top copy, then the mirrored bottom copy.
            var layout = TryReadStrip(bmp, inner, gutter, inner.Y0 + gutter + metaH * (0.5 + offset))
                      ?? TryReadStrip(bmp, inner, gutter, inner.Y1 - gutter - metaH * (0.5 + offset));
            if (layout is not null)
                return layout;
        }
        return null;
    }

    private static Layout? TryReadStrip(Bitmap bmp, InnerRect inner, double gutter, double yCenter)
    {
        double stripW = inner.W - 2 * gutter;
        double moduleW = stripW / Layout.MetaModuleCount;
        var modules = new bool[Layout.MetaModuleCount];
        for (int m = 0; m < Layout.MetaModuleCount; m++)
        {
            double xCenter = inner.X0 + gutter + (m + 0.5) * moduleW;
            var c = bmp.SampleBox(xCenter, yCenter, 1, 1);
            modules[m] = c.R + c.G + c.B < 128 * 3;
        }
        return Layout.UnpackMetadata(modules);
    }

    public Rgb24[] ReadPalette(Bitmap bmp, InnerRect inner, Layout layout)
    {
        // Two calibration strips exist (top and bottom). Measure both and keep the one whose
        // colors track the theoretical palette best — an overlay across one strip then costs nothing.
        var theoretical = palette.Build(layout.BitsPerCell);
        var top = MeasurePaletteStrip(bmp, inner, layout, layout.Gutter + layout.MetaH * 1.5);
        var bottom = MeasurePaletteStrip(bmp, inner, layout, layout.InnerH - layout.Gutter - layout.MetaH * 1.5);
        return StripScore(top, theoretical) <= StripScore(bottom, theoretical) ? top : bottom;
    }

    private static Rgb24[] MeasurePaletteStrip(Bitmap bmp, InnerRect inner, Layout layout, double yEnc)
    {
        double sx = inner.W / layout.InnerW;
        double sy = inner.H / layout.InnerH;
        int count = 1 << layout.BitsPerCell;
        double stripW = layout.InnerW - 2.0 * layout.Gutter;
        double blockW = stripW / count;
        int rx = Math.Clamp((int)((blockW * sx - 2) / 3), 0, 2);
        int ry = Math.Clamp((int)((layout.MetaH * sy - 2) / 3), 0, 2);

        var measured = new Rgb24[count];
        for (int c = 0; c < count; c++)
        {
            double xEnc = layout.Gutter + (c + 0.5) * blockW;
            measured[c] = bmp.SampleBox(inner.X0 + xEnc * sx, inner.Y0 + yEnc * sy, rx, ry);
        }
        return measured;
    }

    /// <summary>Total squared distance between a measured strip and the theoretical palette.</summary>
    private static long StripScore(Rgb24[] measured, Rgb24[] theoretical)
    {
        long score = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            long dr = measured[i].R - theoretical[i].R;
            long dg = measured[i].G - theoretical[i].G;
            long db = measured[i].B - theoretical[i].B;
            score += dr * dr + dg * dg + db * db;
        }
        return score;
    }
}
