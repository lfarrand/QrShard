using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Samples every data cell and classifies it against the measured palette.</summary>
internal static class GridSampler
{
    public static byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, Rgb24[] palette, DecodeScratch scratch)
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
        // Lazy nearest-color lookup keyed on 5-bit-per-channel quantized RGB.
        int[] lut = scratch.ResetNearestColorLut();

        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            double yEnc = layout.DataTop + (gy + 0.5) * layout.CellPx;
            double y = inner.Y0 + yEnc * sy;
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                double xEnc = layout.DataLeft + (gx + 0.5) * layout.CellPx;
                double x = inner.X0 + xEnc * sx;

                int best = 0;
                long bestDist = long.MaxValue;
                foreach (var (dx, dy) in offsets)
                {
                    var c = bmp.SampleBox(x + dx, y + dy, 0, 0);
                    int key = (c.R >> 3 << 10) | (c.G >> 3 << 5) | (c.B >> 3);
                    int v = lut[key];
                    if (v < 0)
                        lut[key] = v = Palette.Nearest(palette, c.R, c.G, c.B);
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
                BitStream.WriteCell(stream, cellIndex * bits, bits, best);
            }
        }
        return stream;
    }
}
