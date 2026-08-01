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

    public PaletteSet ReadPalette(Bitmap bmp, InnerRect inner, Layout layout)
    {
        // Two calibration strips exist (top and bottom). Among the separable copies, the better
        // one (closest to the theoretical palette) drives the classic path — an overlay that
        // collapses colors in one strip then costs nothing. When both strips are individually
        // plausible but differ materially, the capture has a vertical illumination gradient and
        // per-row interpolation between them tracks it.
        var theoretical = palette.Build(layout.BitsPerCell);
        var top = MeasurePaletteStrip(bmp, inner, layout, layout.Gutter + layout.MetaH * 1.5);
        var bottom = MeasurePaletteStrip(bmp, inner, layout, layout.InnerH - layout.Gutter - layout.MetaH * 1.5);
        bool topSeparable = IsSeparable(top), bottomSeparable = IsSeparable(bottom);
        var best = SelectBestMeasured(top, bottom, theoretical, topSeparable, bottomSeparable);

        // Trustworthy = the strip is explainable as per-channel GAIN applied to the theoretical
        // palette. Illumination (screen falloff, room light) is gain-like at any strength, so
        // this accepts even strong gradients — while an overlay or damaged strip fits a pure
        // gain poorly and must not poison interpolation.
        bool interpolate = topSeparable && bottomSeparable &&
                           FitsAsIllumination(top, theoretical) && FitsAsIllumination(bottom, theoretical)
                           && MaxChannelDelta(top, bottom) >= 8;

        // Last resort: the theoretical palette. Both copies sit at the SAME x as each other (the
        // block centre depends only on the block index), so one vertical mark can poison the same
        // entry in both, and picking the "better" of two equally-damaged strips still hands the
        // classifier a broken reference. Nothing downstream noticed — GridSampler consumes Best
        // unguarded, and theoretical was built here only as a yardstick for choosing between the
        // measured pair, never as something to fall back ON.
        //
        // It is always available and correct by construction: SPEC section 3 derives the palette
        // from bitsPerCell alone, so a decoder can regenerate it without reading anything from
        // the image. The measured strip exists to track colour transforms the capture applied —
        // valuable when it is intact, worthless when it is not.
        if (best is null)
            return new PaletteSet(theoretical, theoretical, theoretical, Interpolate: false);
        return new PaletteSet(best, top, bottom, interpolate);
    }

    /// <summary>
    /// Chooses among usable measured copies before comparing colour distance. A collapsed strip
    /// can be numerically closer to the theoretical palette than a healthy strip under strong
    /// illumination gain; distance-first selection would then discard the only usable copy.
    /// </summary>
    internal static Rgb24[]? SelectBestMeasured(Rgb24[] top, Rgb24[] bottom, Rgb24[] theoretical,
        bool? topIsSeparable = null, bool? bottomIsSeparable = null)
    {
        bool topUsable = topIsSeparable ?? IsSeparable(top);
        bool bottomUsable = bottomIsSeparable ?? IsSeparable(bottom);
        if (!topUsable)
            return bottomUsable ? bottom : null;
        if (!bottomUsable)
            return top;
        return StripScore(top, theoretical) <= StripScore(bottom, theoretical) ? top : bottom;
    }

    /// <summary>
    /// Whether a measured palette's entries can still be told apart, which is the only thing a
    /// palette is for. Damage overwrites whole blocks with one colour, collapsing distinct entries
    /// onto each other; two entries the classifier cannot separate make their indices a coin flip,
    /// and at 4 bits a single collapsed pair is order 12% of cells — well past what ECC absorbs.
    ///
    /// The test is the closest pair against the widest pair, which is invariant to gain: dimming
    /// the whole capture shrinks both and leaves the ratio alone, so a legitimately dark or
    /// warm-cast strip still passes. Collapse drives the closest pair toward zero and the ratio
    /// with it. The threshold is deliberately far below any real palette's ratio (0.19 at 4 bits,
    /// 0.06 at 8) so this fires on damage rather than on a difficult capture — being wrong in that
    /// direction would throw away the colour tracking the measured strip exists to provide.
    /// </summary>
    private static bool IsSeparable(Rgb24[] measured)
    {
        if (measured.Length < 2)
            return true;
        long closest = long.MaxValue, widest = 0;
        for (int i = 0; i < measured.Length; i++)
            for (int j = i + 1; j < measured.Length; j++)
            {
                long dr = measured[i].R - measured[j].R;
                long dg = measured[i].G - measured[j].G;
                long db = measured[i].B - measured[j].B;
                long d = dr * dr + dg * dg + db * db;
                if (d < closest) closest = d;
                if (d > widest) widest = d;
            }
        // An all-one-colour strip (widest 0) is total collapse, not a pass.
        return widest > 0 && closest * SeparabilityDivisor >= widest;
    }

    /// <summary>
    /// Closest pair must be at least 1/this of the widest. A 4-bit theoretical palette sits at
    /// about 1/26 and an 8-bit one at about 1/260 by squared distance, so 4000 clears every real
    /// palette by a wide margin while still catching a pair collapsed to near-zero separation.
    /// </summary>
    private const long SeparabilityDivisor = 4000;

    /// <summary>Test seam: the illumination gate is the discriminator two decode paths hang off,
    /// and it is worth pinning directly rather than only through a full round trip.</summary>
    internal static bool FitsAsIlluminationForTests(Rgb24[] measured, Rgb24[] theoretical) =>
        FitsAsIllumination(measured, theoretical);

    private static bool FitsAsIllumination(Rgb24[] measured, Rgb24[] theoretical)
    {
        Span<double> gain = stackalloc double[3];
        double numR = 0, numG = 0, numB = 0, denR = 0, denG = 0, denB = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            numR += (double)measured[i].R * theoretical[i].R;
            denR += (double)theoretical[i].R * theoretical[i].R;
            numG += (double)measured[i].G * theoretical[i].G;
            denG += (double)theoretical[i].G * theoretical[i].G;
            numB += (double)measured[i].B * theoretical[i].B;
            denB += (double)theoretical[i].B * theoretical[i].B;
        }
        gain[0] = denR > 0 ? numR / denR : 1;
        gain[1] = denG > 0 ? numG / denG : 1;
        gain[2] = denB > 0 ? numB / denB : 1;
        foreach (double g in gain)
            if (g is < 0.2 or > 1.6)
                return false;

        double residual = 0, worstEntry = 0;
        for (int i = 0; i < measured.Length; i++)
        {
            double dr = Math.Abs(measured[i].R - gain[0] * theoretical[i].R);
            double dg = Math.Abs(measured[i].G - gain[1] * theoretical[i].G);
            double db = Math.Abs(measured[i].B - gain[2] * theoretical[i].B);
            residual += dr + dg + db;
            worstEntry = Math.Max(worstEntry, Math.Max(dr, Math.Max(dg, db)));
        }
        // The MEAN alone was not enough, and the gap it left mattered. Illumination applies a gain
        // to every entry at once; it cannot displace ONE entry and leave the rest. But one bad
        // block out of sixteen contributes only about a sixteenth of a mean taken over 48 channel
        // samples, so a single obscured block could move ~246 per channel and still "fit".
        //
        // That let an overlay across ONE strip through the gate, which then made things actively
        // worse: the damage itself creates the top/bottom delta that switches interpolation ON,
        // and ReadInterpolated lerps Top and Bottom without ever consulting Best. So the healthy
        // strip was picked as Best and then not used, while the damaged one poisoned every row.
        // Damaging one strip was fatal where damaging BOTH the same way decoded fine — the
        // redundancy inverted.
        //
        // Capping the worst single deviation is the check that matches the physics. A strip that
        // fails it is not badly lit, it is obscured, and the right answer is to stop interpolating
        // and use the other copy.
        return residual / (measured.Length * 3) < 28 && worstEntry < MaxEntryResidual;
    }

    /// <summary>
    /// Largest per-channel deviation from the gain-predicted value that still counts as
    /// illumination rather than damage. Sampling noise, mild JPEG and small geometry error sit
    /// well under this; a displaced palette block starts costing decodes around 55.
    /// </summary>
    private const double MaxEntryResidual = 48;

    private static int MaxChannelDelta(Rgb24[] a, Rgb24[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Abs(a[i].R - b[i].R));
            max = Math.Max(max, Math.Abs(a[i].G - b[i].G));
            max = Math.Max(max, Math.Abs(a[i].B - b[i].B));
        }
        return max;
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
