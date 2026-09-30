using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Samples every data cell and classifies it against the measured palette.</summary>
internal sealed class GridSampler(Palette paletteMath, BitStream bitStream) : IGridSampler
{
    public GridSampler() : this(new Palette(), new BitStream())
    {
    }

    private static readonly (int dx, int dy)[] CenterOnly = [(0, 0)];

    private static readonly (int dx, int dy)[] NineOffsets =
        [(0, 0), (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];

    /// <summary>
    /// Fallback squared-distance floor when a palette has no measurable spacing. Real palettes
    /// use <see cref="ConfidenceFloorSquared"/> instead: 8-bit neighbours (step 36) land at 196,
    /// and 10-bit red neighbours (step 17) land at 49.
    /// </summary>
    internal const long DefaultConfidentDist = 200;

    /// <summary>Squared distance beyond which a sample is suspect regardless of margin.</summary>
    internal const long AbsoluteSuspectDist = 4000;

    public byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes, DecodeScratch scratch,
        out bool[]? suspectBytes, out byte[]? secondChoiceBytes, int[]? cellMargins = null,
        bool[]? ambiguousCells = null, bool[]? nearTieCells = null)
    {
        double sx = inner.W / layout.InnerW;
        double sy = inner.H / layout.InnerH;
        double cellW = layout.CellPx * sx, cellH = layout.CellPx * sy;

        // Candidate sample offsets around each cell center: when a capture is rescaled, the
        // exact center may land on a blended boundary pixel; picking the candidate closest to a
        // palette color strongly prefers pure interior pixels. Offsets must stay inside the cell.
        var offsets = Math.Min(cellW, cellH) >= 3.5 ? NineOffsets : CenterOnly;

        int bits = layout.BitsPerCell;
        // Defense in depth: Layout.UnpackMetadata already bounds the geometry, but guard the
        // allocation site directly so no path can size a negative or absurd buffer from TotalBits.
        if (layout.TotalBytes is < 0 or > Layout.MaxCellStreamBytes)
            throw new ShardDecodeException("Shard metadata declares an implausible data-grid size.");
        // Tie the DECLARED geometry to the image actually in hand. Every previous bound compares
        // the strip's fields against the encoder's maxima, and nothing compared them against the
        // bitmap — so `sx = inner.W / layout.InnerW` just became a tiny scale factor, every sample
        // coordinate clamped into range, and a few-KB image sized buffers for the declared grid.
        // metaH = cellPx = 1 admits gridW 16382 x gridH 16378, which slips just under the cell
        // ceiling above at ~268M cells and asks for well over a GB of scratch, per worker.
        //
        // A cell cannot be resolved from less than one pixel, so the grid can never legitimately
        // be finer than the inner rectangle it was captured into. A real capture satisfies this
        // with room to spare (inner.W is about layout.InnerW >= GridW * CellPx); only a capture
        // downscaled past one pixel per cell trips it, and that is unrecoverable regardless.
        layout.RequireResolvableIn(inner.W, inner.H);
        int streamLength = (int)((layout.TotalBits + 7) / 8);
        byte[] stream = scratch.ClearedCells(streamLength);
        // Ambiguity flags + second-choice values feed erasure and Chase decoding — only
        // meaningful when ECC is present.
        bool[]? suspects = layout.EccParity > 0 ? scratch.ClearedSuspects(streamLength) : null;
        byte[]? second = layout.EccParity > 0 ? scratch.ClearedSecondChoice(streamLength) : null;

        // Uniform palettes: once per image. Interpolated rows are measured inside ReadInterpolated,
        // because a row between the strips can be tighter than either strip.
        long confidenceFloor = palettes.Interpolate ? 0 : ConfidenceFloorFor(palettes);
        if (!palettes.Interpolate)
            scratch.UniformConfidenceFloor = confidenceFloor;
        if (palettes.Interpolate)
            ReadInterpolated(bmp, inner, layout, palettes, offsets, stream, suspects, second, sx, sy, bits, cellMargins, scratch, ambiguousCells, nearTieCells);
        else
            ReadUniform(bmp, inner, layout, palettes.Best, offsets, stream, suspects, second, scratch, sx, sy, bits, cellMargins, confidenceFloor, ambiguousCells, nearTieCells);
        suspectBytes = suspects;
        secondChoiceBytes = second;
        return stream;
    }

    /// <summary>
    /// Records classification confidence: an uncertain cell (far from every palette color, or
    /// nearly equidistant to a second one) has its bytes flagged as erasure candidates and its
    /// runner-up value written to the second-choice stream — the raw material for Chase
    /// decoding. Confident cells write their winning value to both streams, so a byte-level
    /// splice of the two streams flips exactly the ambiguous cells.
    ///
    /// <paramref name="confidenceFloor"/> is the largest squared distance at which a runner-up
    /// one measured palette step away cannot satisfy the near-tie test. Exact hits (distance 0)
    /// skip the scan, except on a row whose floor is 0: two indices can then share a colour, and
    /// a sample of that colour is a 0-vs-0 tie. <c>secondDist &lt; bestDist * 2</c> is false when
    /// both distances are 0, so the tie has to be flagged on its own. A unique exact colour on
    /// the same row still has a positive runner-up and stays confident. A sample that lands on a
    /// different palette colour also has distance 0, so distance alone cannot mark it.
    /// </summary>
    private void RecordConfidence(bool[]? suspects, byte[]? second, Rgb24[] palette, int best, long bestDist,
        byte r, byte g, byte b, long cellIndex, int bits, long confidenceFloor, bool[]? ambiguousCells,
        bool[]? nearTieCells, SeparablePalette? product, MeasuredColorIndex? measured)
    {
        int alternative = best;
        bool far = bestDist > AbsoluteSuspectDist;
        // Floor 0 is the only floor at which an exact hit can be two indices of one colour.
        bool maybeExactTie = bestDist == 0 && confidenceFloor == 0;
        // Markers are recorded even when ECC is off, because erasure flags are not allocated then.
        bool wantRunnerUp = suspects is not null || ambiguousCells is not null || nearTieCells is not null;
        if (wantRunnerUp && (bestDist > confidenceFloor || far || maybeExactTie))
        {
            int secondIndex = RunnerUp(product, measured, palette, r, g, b, best, out long secondDist);
            bool exactTie = bestDist == 0 && secondDist == 0;
            bool nearTie = !exactTie && !far && secondDist < bestDist * 2;
            if (exactTie && ambiguousCells is not null)
                ambiguousCells[(int)cellIndex] = true;
            // Distance 64 on a 10-bit row is above the floor and would otherwise paint almost green.
            if (nearTie && nearTieCells is not null)
                nearTieCells[(int)cellIndex] = true;
            if (suspects is not null && (far || nearTie || exactTie))
            {
                alternative = secondIndex;
                long firstBit = cellIndex * bits;
                long firstByte = firstBit >> 3, lastByte = (firstBit + bits - 1) >> 3;
                for (long i = firstByte; i <= lastByte && i < suspects.Length; i++)
                    suspects[i] = true;
            }
        }
        if (second is not null)
            bitStream.WriteCell(second, cellIndex * bits, bits, alternative);
    }

    private int RunnerUp(SeparablePalette? product, MeasuredColorIndex? measured, Rgb24[] palette,
        int r, int g, int b, int best, out long distance)
    {
        if (product is not null)
            return product.SecondNearest(palette, r, g, b, best, out distance);
        if (measured is not null)
            return measured.SecondNearest(r, g, b, best, out distance);
        return paletteMath.SecondNearest(palette, r, g, b, best, out distance);
    }

    /// <summary>
    /// Largest squared sample distance at which even a colinear runner-up one palette step away
    /// fails <c>secondDist &lt; 2 * bestDist</c>. Derived from <c>(S² − d²)² &gt;= 4 d² S²</c>.
    /// </summary>
    internal static long ConfidenceFloorSquared(long minSepSq)
    {
        if (minSepSq <= 0)
            return 0;
        // ClosestSquared's empty-palette sentinel. A real RGB pair cannot exceed the cube diagonal.
        if (minSepSq > 3L * 255 * 255)
            return DefaultConfidentDist;
        long floor = 0;
        for (int d = 0; d <= 255; d++)
        {
            long dd = (long)d * d;
            if (dd > minSepSq)
                break;
            long left = minSepSq - dd;
            if (left * left < 4 * dd * minSepSq)
                break;
            floor = dd;
        }
        return floor;
    }

    /// <summary>
    /// Floor for the palette the sampler will classify against. When the strips are interpolated,
    /// <paramref name="layout"/> selects the rows actually classified: a midpoint can pull two
    /// colours together even though each strip still has a wide closest pair.
    /// </summary>
    internal static long ConfidenceFloorFor(PaletteSet palettes, Layout? layout = null)
    {
        if (!palettes.Interpolate)
            return ConfidenceFloorSquared(Palette.ClosestSquared(palettes.Best));
        if (layout is null)
            return ConfidenceFloorSquared(Palette.ClosestSquared(palettes.Best));
        return MinimumInterpolatedFloor(palettes, layout);
    }

    /// <summary>Tightest confidence floor among the row palettes <see cref="ReadInterpolated"/> builds.</summary>
    private static long MinimumInterpolatedFloor(PaletteSet palettes, Layout layout)
    {
        var row = new Rgb24[palettes.Top.Length];
        long floor = long.MaxValue;
        for (int gy = 0; gy < layout.GridH; gy++)
            floor = Math.Min(floor, RowConfidenceFloor(palettes, layout, gy, row));
        return floor == long.MaxValue ? 0 : floor;
    }

    private static void FillInterpolatedRow(PaletteSet palettes, Layout layout, int gy, Rgb24[] rowPalette)
    {
        double t = InterpolatedRowT(layout, gy);
        for (int c = 0; c < rowPalette.Length; c++)
            rowPalette[c] = Lerp(palettes.Top[c], palettes.Bottom[c], t);
    }

    private static long RowConfidenceFloor(PaletteSet palettes, Layout layout, int gy, Rgb24[] rowPalette)
    {
        FillInterpolatedRow(palettes, layout, gy, rowPalette);
        var index = new SeparablePalette();
        long closest = index.TryRebuild(rowPalette)
            ? index.ClosestSquared
            : Palette.ClosestSquared(rowPalette);
        return ConfidenceFloorSquared(closest);
    }

    /// <summary>Blend factor for grid row <paramref name="gy"/>, matching the strip positions the renderer uses.</summary>
    internal static double InterpolatedRowT(Layout layout, int gy)
    {
        double yTopStrip = layout.Gutter + layout.MetaH * 1.5;
        double yBottomStrip = layout.InnerH - layout.Gutter - layout.MetaH * 1.5;
        double yEnc = layout.DataTop + (gy + 0.5) * layout.CellPx;
        double span = yBottomStrip - yTopStrip;
        return span == 0 ? 0 : Math.Clamp((yEnc - yTopStrip) / span, 0, 1);
    }

    /// <summary>
    /// Precomputed pixel coordinates: x depends only on the column and y only on the row, so
    /// the per-cell work collapses to array lookups instead of floating-point math.
    /// </summary>
    private static int[] ColumnPixels(InnerRect inner, Layout layout, double sx, int width)
    {
        var cols = new int[layout.GridW];
        for (int gx = 0; gx < layout.GridW; gx++)
        {
            double xEnc = layout.DataLeft + (gx + 0.5) * layout.CellPx;
            cols[gx] = Math.Clamp((int)Math.Floor(inner.X0 + xEnc * sx), 0, width - 1);
        }
        return cols;
    }

    private static int RowPixel(InnerRect inner, Layout layout, double sy, int height, int gy)
    {
        double yEnc = layout.DataTop + (gy + 0.5) * layout.CellPx;
        return Math.Clamp((int)Math.Floor(inner.Y0 + yEnc * sy), 0, height - 1);
    }

    /// <summary>
    /// Uniform-path classify. A product palette uses <paramref name="index"/>, which matches the
    /// scan's lowest-index tie. Otherwise reuse the 5-bit LUT for an exact hit and refine a
    /// nonzero distance with <paramref name="measured"/> so a gain-0.2 cube collision is not trusted.
    /// </summary>
    private int ClassifyUniform(int[]? lut, Rgb24[] palette, SeparablePalette? index, MeasuredColorIndex measured,
        byte r, byte g, byte b, out long dist)
    {
        if (index is not null)
        {
            int indexed = index.Nearest(r, g, b);
            long idr = r - palette[indexed].R, idg = g - palette[indexed].G, idb = b - palette[indexed].B;
            dist = idr * idr + idg * idg + idb * idb;
            return indexed;
        }

        // The 5-bit cache still catches a repeated exact hit. Anything else goes through the
        // spatial index, which agrees with the scan including a lowest-index tie.
        int key = (r >> 3 << 10) | (g >> 3 << 5) | (b >> 3);
        int v = lut![key];
        if (v < 0)
        {
            lut[key] = v = measured.Nearest(r, g, b, out dist);
            return v;
        }

        long dr = r - palette[v].R, dg = g - palette[v].G, db = b - palette[v].B;
        dist = dr * dr + dg * dg + db * db;
        return dist == 0 ? v : measured.Nearest(r, g, b, out dist);
    }

    private void ReadUniform(Bitmap bmp, InnerRect inner, Layout layout, Rgb24[] palette,
        (int dx, int dy)[] offsets, byte[] stream, bool[]? suspects, byte[]? second, DecodeScratch scratch,
        double sx, double sy, int bits, int[]? cellMargins, long confidenceFloor, bool[]? ambiguousCells,
        bool[]? nearTieCells)
    {
        // A measured product palette has an exact per-channel index (same tie order as the scan).
        // A strip knocked off that grid uses the spatial lookup, which keeps the same tie.
        var separable = new SeparablePalette();
        SeparablePalette? index = separable.TryRebuild(palette) ? separable : null;
        var measured = new MeasuredColorIndex();
        if (index is null)
            measured.Rebuild(palette);
        int[]? lut = index is null ? scratch.ResetNearestColorLut() : null;
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);

        // Interior cells (the overwhelming majority) index with precomputed flat deltas — no
        // clamping, no per-offset coordinate math; only cells within one pixel of the capture
        // edge take the clamped path. The array is cached on DecodeScratch to avoid per-call allocation.
        var deltas = scratch.Deltas(offsets.Length);
        for (int k = 0; k < offsets.Length; k++)
            deltas[k] = offsets[k].dy * width + offsets[k].dx;

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            int rowY = RowPixel(inner, layout, sy, height, gy);
            bool rowInterior = rowY >= 1 && rowY < height - 1;
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int colX = cols[gx];
                int best = 0;
                long bestDist = long.MaxValue;
                byte bR = 0, bG = 0, bB = 0;
                if (rowInterior && colX >= 1 && colX < width - 1)
                {
                    int baseIndex = rowY * width + colX;
                    foreach (int delta in deltas)
                    {
                        var c = px[baseIndex + delta];
                        int v = ClassifyUniform(lut, palette, index, measured, c.R, c.G, c.B, out long dist);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = v;
                            (bR, bG, bB) = (c.R, c.G, c.B);
                            if (dist == 0)
                                break;
                        }
                    }
                }
                else
                {
                    foreach (var (dx, dy) in offsets)
                    {
                        int xi = Math.Clamp(colX + dx, 0, width - 1);
                        int yi = Math.Clamp(rowY + dy, 0, height - 1);
                        var c = px[yi * width + xi];
                        int v = ClassifyUniform(lut, palette, index, measured, c.R, c.G, c.B, out long dist);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = v;
                            (bR, bG, bB) = (c.R, c.G, c.B);
                            if (dist == 0)
                                break;
                        }
                    }
                }
                bitStream.WriteCell(stream, cellIndex * bits, bits, best);
                RecordConfidence(suspects, second, palette, best, bestDist, bR, bG, bB, cellIndex, bits, confidenceFloor,
                    ambiguousCells, nearTieCells, index, index is null ? measured : null);
                if (cellMargins is not null)
                    cellMargins[(int)cellIndex] = (int)Math.Min(bestDist, int.MaxValue);
            }
        }
    }

    /// <summary>
    /// Gradient path: the reference palette is re-interpolated per grid row between the top and
    /// bottom calibration strips, so a vertical illumination ramp (screen falloff, room light in
    /// a photo) moves the classification targets with it. The quantized LUT of the uniform path
    /// cannot be reused — it would have to be rebuilt every row — so classification instead goes
    /// through the exact per-channel tables of <see cref="SeparablePalette"/>, which cost one
    /// rebuild per row and are valid whenever the row palette kept its product structure.
    /// </summary>
    private void ReadInterpolated(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes,
        (int dx, int dy)[] offsets, byte[] stream, bool[]? suspects, byte[]? second, double sx, double sy, int bits,
        int[]? cellMargins, DecodeScratch scratch, bool[]? ambiguousCells, bool[]? nearTieCells)
    {
        var rowPalette = new Rgb24[palettes.Top.Length];
        var rowIndex = new SeparablePalette();
        var measured = new MeasuredColorIndex();
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);
        // Heatmap rows need the floors even when this image has no ECC to flag.
        long[]? rowFloors = cellMargins is not null ? scratch.RowFloors(layout.GridH) : null;
        // The tie marker is meaningful on a no-ECC diagnose, which still passes cell margins.
        // Keep the measured row floor in that case so a unique exact hit is not treated as a tie.
        bool needFloor = suspects is not null || rowFloors is not null || ambiguousCells is not null;
        ClosestPairScratch? pairs = needFloor ? scratch.ClosestPairs : null;

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            // Spacing is per row. Swapping two colours between the strips leaves both endpoints
            // a full step apart while the midpoint entries coincide.
            FillInterpolatedRow(palettes, layout, gy, rowPalette);
            bool separable = rowIndex.TryRebuild(rowPalette);
            if (!separable)
                measured.Rebuild(rowPalette);
            long rowFloor = 0;
            if (needFloor)
            {
                long closest = separable
                    ? rowIndex.ClosestSquared
                    : Palette.ClosestSquared(rowPalette, pairs);
                rowFloor = ConfidenceFloorSquared(closest);
                if (rowFloors is not null)
                    rowFloors[gy] = rowFloor;
            }

            int rowY = RowPixel(inner, layout, sy, height, gy);
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int colX = cols[gx];
                int best = 0;
                long bestDist = long.MaxValue;
                byte bR = 0, bG = 0, bB = 0;
                foreach (var (dx, dy) in offsets)
                {
                    int xi = Math.Clamp(colX + dx, 0, width - 1);
                    int yi = Math.Clamp(rowY + dy, 0, height - 1);
                    var c = px[yi * width + xi];
                    int v;
                    long dist;
                    if (separable)
                    {
                        v = rowIndex.Nearest(c.R, c.G, c.B);
                        long dr = c.R - rowPalette[v].R, dg = c.G - rowPalette[v].G, db = c.B - rowPalette[v].B;
                        dist = dr * dr + dg * dg + db * db;
                    }
                    else
                        v = measured.Nearest(c.R, c.G, c.B, out dist);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = v;
                        (bR, bG, bB) = (c.R, c.G, c.B);
                        if (dist == 0)
                            break;
                    }
                }
                bitStream.WriteCell(stream, cellIndex * bits, bits, best);
                RecordConfidence(suspects, second, rowPalette, best, bestDist, bR, bG, bB, cellIndex, bits, rowFloor,
                    ambiguousCells, nearTieCells, separable ? rowIndex : null, separable ? null : measured);
                if (cellMargins is not null)
                    cellMargins[(int)cellIndex] = (int)Math.Min(bestDist, int.MaxValue);
            }
        }
    }

    private static Rgb24 Lerp(Rgb24 a, Rgb24 b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t + 0.5),
        (byte)(a.G + (b.G - a.G) * t + 0.5),
        (byte)(a.B + (b.B - a.B) * t + 0.5));
}
