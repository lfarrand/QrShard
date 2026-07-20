using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Samples every data cell and classifies it against the measured palette.</summary>
internal sealed class GridSampler(Palette paletteMath, BitStream bitStream) : IGridSampler
{
    public GridSampler() : this(new Palette(), new BitStream())
    {
    }

    public byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes, DecodeScratch scratch)
    {
        double sx = inner.W / layout.InnerW;
        double sy = inner.H / layout.InnerH;
        double cellW = layout.CellPx * sx, cellH = layout.CellPx * sy;

        // Candidate sample offsets around each cell center: when a capture is rescaled, the
        // exact center may land on a blended boundary pixel; picking the candidate closest to a
        // palette color strongly prefers pure interior pixels. Offsets must stay inside the cell.
        var offsets = new List<(int dx, int dy)> { (0, 0) };
        if (Math.Min(cellW, cellH) >= 3.5)
            offsets.AddRange([(-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)]);

        int bits = layout.BitsPerCell;
        byte[] stream = scratch.ClearedCells((int)((layout.TotalBits + 7) / 8));

        if (palettes.Interpolate)
            ReadInterpolated(bmp, inner, layout, palettes, offsets, stream, sx, sy, bits);
        else
            ReadUniform(bmp, inner, layout, palettes.Best, offsets, stream, scratch, sx, sy, bits);
        return stream;
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
        List<(int dx, int dy)> offsets, byte[] stream, DecodeScratch scratch, double sx, double sy, int bits)
    {
        // Lazy nearest-color lookup keyed on 5-bit-per-channel quantized RGB.
        int[] lut = scratch.ResetNearestColorLut();
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);
        var offsetArray = offsets.ToArray();

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            int rowY = RowPixel(inner, layout, sy, height, gy);
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int colX = cols[gx];
                int best = 0;
                long bestDist = long.MaxValue;
                foreach (var (dx, dy) in offsetArray)
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
                        if (dist == 0)
                            break;
                    }
                }
                bitStream.WriteCell(stream, cellIndex * bits, bits, best);
            }
        }
    }

    /// <summary>
    /// Gradient path: the reference palette is re-interpolated per grid row between the top and
    /// bottom calibration strips, so a vertical illumination ramp (screen falloff, room light in
    /// a photo) moves the classification targets with it. No LUT — the palette changes per row.
    /// </summary>
    private void ReadInterpolated(Bitmap bmp, InnerRect inner, Layout layout, PaletteSet palettes,
        List<(int dx, int dy)> offsets, byte[] stream, double sx, double sy, int bits)
    {
        double yTopStrip = layout.Gutter + layout.MetaH * 1.5;
        double yBottomStrip = layout.InnerH - layout.Gutter - layout.MetaH * 1.5;
        var rowPalette = new Rgb24[palettes.Best.Length];
        int width = bmp.Width, height = bmp.Height;
        var px = bmp.Px;
        int[] cols = ColumnPixels(inner, layout, sx, width);
        var offsetArray = offsets.ToArray();

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
                foreach (var (dx, dy) in offsetArray)
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
                        if (dist == 0)
                            break;
                    }
                }
                bitStream.WriteCell(stream, cellIndex * bits, bits, best);
            }
        }
    }

    private static Rgb24 Lerp(Rgb24 a, Rgb24 b, double t) => new(
        (byte)(a.R + (b.R - a.R) * t + 0.5),
        (byte)(a.G + (b.G - a.G) * t + 0.5),
        (byte)(a.B + (b.B - a.B) * t + 0.5));
}
