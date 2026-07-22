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

    /// <summary>Squared-distance floor below which a classification is trusted outright.</summary>
    private const long ConfidentDist = 200;

    /// <summary>Squared distance beyond which a sample is suspect regardless of margin.</summary>
    private const long AbsoluteSuspectDist = 4000;

    public byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes, DecodeScratch scratch,
        out bool[]? suspectBytes, out byte[]? secondChoiceBytes)
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
        if (layout.TotalBytes is < 0 or > (long)Layout.MaxResolution * Layout.MaxResolution)
            throw new ShardDecodeException("Shard metadata declares an implausible data-grid size.");
        int streamLength = (int)((layout.TotalBits + 7) / 8);
        byte[] stream = scratch.ClearedCells(streamLength);
        // Ambiguity flags + second-choice values feed erasure and Chase decoding — only
        // meaningful when ECC is present.
        bool[]? suspects = layout.EccParity > 0 ? scratch.ClearedSuspects(streamLength) : null;
        byte[]? second = layout.EccParity > 0 ? scratch.ClearedSecondChoice(streamLength) : null;

        if (palettes.Interpolate)
            ReadInterpolated(bmp, inner, layout, palettes, offsets, stream, suspects, second, sx, sy, bits);
        else
            ReadUniform(bmp, inner, layout, palettes.Best, offsets, stream, suspects, second, scratch, sx, sy, bits);
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
    /// </summary>
    private void RecordConfidence(bool[]? suspects, byte[]? second, Rgb24[] palette, int best, long bestDist,
        byte r, byte g, byte b, long cellIndex, int bits)
    {
        int alternative = best;
        if (suspects is not null && bestDist > ConfidentDist)
        {
            int secondIndex = paletteMath.SecondNearest(palette, r, g, b, best, out long secondDist);
            if (bestDist > AbsoluteSuspectDist || secondDist < bestDist * 2)
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

    private void ReadUniform(Bitmap bmp, InnerRect inner, Layout layout, Rgb24[] palette,
        (int dx, int dy)[] offsets, byte[] stream, bool[]? suspects, byte[]? second, DecodeScratch scratch,
        double sx, double sy, int bits)
    {
        // Lazy nearest-color lookup keyed on 5-bit-per-channel quantized RGB.
        int[] lut = scratch.ResetNearestColorLut();
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);

        // Interior cells (the overwhelming majority) index with precomputed flat deltas — no
        // clamping, no per-offset coordinate math; only cells within one pixel of the capture
        // edge take the clamped path.
        var deltas = new int[offsets.Length];
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
                        int key = (c.R >> 3 << 10) | (c.G >> 3 << 5) | (c.B >> 3);
                        int v = lut[key];
                        if (v < 0)
                            lut[key] = v = paletteMath.Nearest(palette, c.R, c.G, c.B);
                        long dr = c.R - palette[v].R, dg = c.G - palette[v].G, db = c.B - palette[v].B;
                        long dist = dr * dr + dg * dg + db * db;
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
                        int key = (c.R >> 3 << 10) | (c.G >> 3 << 5) | (c.B >> 3);
                        int v = lut[key];
                        if (v < 0)
                            lut[key] = v = paletteMath.Nearest(palette, c.R, c.G, c.B);
                        long dr = c.R - palette[v].R, dg = c.G - palette[v].G, db = c.B - palette[v].B;
                        long dist = dr * dr + dg * dg + db * db;
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
                RecordConfidence(suspects, second, palette, best, bestDist, bR, bG, bB, cellIndex, bits);
            }
        }
    }

    /// <summary>
    /// Gradient path: the reference palette is re-interpolated per grid row between the top and
    /// bottom calibration strips, so a vertical illumination ramp (screen falloff, room light in
    /// a photo) moves the classification targets with it. No LUT — the palette changes per row.
    /// </summary>
    private void ReadInterpolated(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes,
        (int dx, int dy)[] offsets, byte[] stream, bool[]? suspects, byte[]? second, double sx, double sy, int bits)
    {
        double yTopStrip = layout.Gutter + layout.MetaH * 1.5;
        double yBottomStrip = layout.InnerH - layout.Gutter - layout.MetaH * 1.5;
        var rowPalette = new Rgb24[palettes.Best.Length];
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            double yEnc = layout.DataTop + (gy + 0.5) * layout.CellPx;
            double t = Math.Clamp((yEnc - yTopStrip) / (yBottomStrip - yTopStrip), 0, 1);
            for (int c = 0; c < rowPalette.Length; c++)
                rowPalette[c] = Lerp(palettes.Top[c], palettes.Bottom[c], t);

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
                    int v = paletteMath.Nearest(rowPalette, c.R, c.G, c.B);
                    long dr = c.R - rowPalette[v].R, dg = c.G - rowPalette[v].G, db = c.B - rowPalette[v].B;
                    long dist = dr * dr + dg * dg + db * db;
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
                RecordConfidence(suspects, second, rowPalette, best, bestDist, bR, bG, bB, cellIndex, bits);
            }
        }
    }

    private static Rgb24 Lerp(Rgb24 a, Rgb24 b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t + 0.5),
        (byte)(a.G + (b.G - a.G) * t + 0.5),
        (byte)(a.B + (b.B - a.B) * t + 0.5));
}
