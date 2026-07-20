using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>Per-worker reusable buffers: the pixel canvas plus the stream/cell staging arrays.</summary>
internal sealed class RenderScratch(Layout layout)
{
    public readonly Rgb24[] Pixels = new Rgb24[layout.Width * layout.Height];
    private byte[]? _stream;
    private byte[]? _cells;

    public byte[] Stream(int length)
    {
        if (_stream is null || _stream.Length < length)
            _stream = new byte[length];
        return _stream;
    }

    public byte[] Cells(int length)
    {
        if (_cells is null || _cells.Length < length)
            _cells = new byte[length];
        return _cells;
    }

    private byte[]? _scatter;

    public byte[] ScatterBuffer(int length)
    {
        if (_scatter is null || _scatter.Length < length)
            _scatter = new byte[length];
        return _scatter;
    }
}

/// <summary>How rendered pixels are written to disk for the chosen container format.</summary>
internal sealed class ShardImageWriter(string format, Layout layout, AppSettings cfg, FastPng fastPng, ShardImageFormat formats)
{
    private readonly bool _fastPng = format == "png";
    private readonly SixLabors.ImageSharp.Formats.IImageEncoder? _encoder = formats.CreateEncoder(format);
    private readonly bool _upFilter = layout.CellPx >= 2;

    // The configured level applies where compression pays off (Up-filtered repeated rows);
    // 1 px noise cells are incompressible by construction, so they always use Fastest.
    private readonly System.IO.Compression.CompressionLevel _pngLevel = layout.CellPx >= 2
        ? cfg.PngCompressionLevel
        : System.IO.Compression.CompressionLevel.Fastest;

    public void Write(string path, Rgb24[] px, int width, int height)
    {
        if (_fastPng)
        {
            fastPng.Write(path, px.AsSpan(0, width * height), width, height, _upFilter, _pngLevel);
            return;
        }
        using var image = Image.LoadPixelData<Rgb24>(
            ShardImageFormat.EncodeConfiguration, px.AsSpan(0, width * height), width, height);
        image.Save(path, _encoder!);
    }
}

/// <summary>Renders one shard's cell stream into pixels and writes the image file.</summary>
internal interface IShardRenderer
{
    ShardImageWriter CreateWriter(string format, Layout layout, AppSettings cfg);

    void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream, int streamLength,
        string outPath, RenderScratch scratch, ShardImageWriter writer);
}

/// <summary>
/// The encode-side rasterizer: ECC-protects the staged stream, then draws the locator frame,
/// camera finder bands, metadata/palette strips, and the data grid into a pixel canvas.
/// </summary>
internal sealed class ShardRenderer(Fec fec, BitStream bitStream, FastPng fastPng, ShardImageFormat formats,
    Interleaver2 interleaver) : IShardRenderer
{
    public ShardRenderer() : this(new Fec(), new BitStream(), new FastPng(), new ShardImageFormat(), new Interleaver2())
    {
    }

    public ShardImageWriter CreateWriter(string format, Layout layout, AppSettings cfg) =>
        new(format, layout, cfg, fastPng, formats);

    /// <summary>The stream buffer holds [0..headerSize) header then payload, streamLength bytes total.</summary>
    public void RenderShard(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] stream, int streamLength,
        string outPath, RenderScratch scratch, ShardImageWriter writer)
    {
        byte[] cellBuffer;
        if (layout.EccParity > 0)
        {
            // The renderer reads TotalBytes; FEC fills the first CodewordCount*255 of them,
            // so clear the sub-codeword tail to keep pooled-buffer reuse deterministic.
            int cellLength = (int)layout.TotalBytes;
            cellBuffer = scratch.Cells(cellLength);
            fec.ProtectInto(stream, streamLength, layout.EccParity, layout.CodewordCount, cellBuffer);
            int protectedLength = layout.CodewordCount * Fec.CodewordLength;
            if (protectedLength < cellLength)
                Array.Clear(cellBuffer, protectedLength, cellLength - protectedLength);
            if (layout.Interleave2)
            {
                // v2 interleave: scatter the classic layout through the seeded permutation.
                byte[] scattered = scratch.ScatterBuffer(cellLength);
                interleaver.Scatter(cellBuffer, scattered, protectedLength);
                Array.Copy(cellBuffer, protectedLength, scattered, protectedLength, cellLength - protectedLength);
                cellBuffer = scattered;
            }
        }
        else
        {
            // Without ECC the stream itself is the cell buffer; the caller allocated it exactly
            // sized in this mode, because the renderer treats bytes past the end as zero cells.
            cellBuffer = stream;
        }
        Render(layout, palette, metaModules, cellBuffer, outPath, scratch.Pixels, writer);
    }

    private void Render(Layout layout, Rgb24[] palette, byte[] metaModules, byte[] cellBuffer, string outPath,
        Rgb24[] px, ShardImageWriter writer)
    {
        int w = layout.Width, h = layout.Height;
        var white = new Rgb24(255, 255, 255);
        var black = new Rgb24(0, 0, 0);
        Array.Fill(px, white);

        // Camera profile shifts the frame + inner content down below the top finder band.
        int oy = layout.ContentTop;
        int contentH = h - 2 * layout.FinderBand;

        // Locator frame ring.
        int f0 = Layout.QuietPx, f1 = Layout.Border; // frame spans [f0, f1) from each content edge
        FillRect(px, w, f0, oy + f0, w - 2 * f0, Layout.FramePx, black);               // top
        FillRect(px, w, f0, oy + contentH - f1, w - 2 * f0, Layout.FramePx, black);    // bottom
        FillRect(px, w, f0, oy + f0, Layout.FramePx, contentH - 2 * f0, black);        // left
        FillRect(px, w, w - f1, oy + f0, Layout.FramePx, contentH - 2 * f0, black);    // right

        if (layout.CameraFinders)
            DrawFinderBands(px, w, h, layout);

        int ix = Layout.Border, iy = oy + Layout.Border; // inner-area origin
        int gutter = layout.Gutter;

        // Metadata + palette strips, duplicated top and bottom for overlay resilience.
        DrawMetaStrip(px, w, layout, metaModules, ix, iy + gutter);
        DrawPaletteStrip(px, w, layout, palette, ix, iy + gutter + layout.MetaH);
        DrawPaletteStrip(px, w, layout, palette, ix, iy + layout.InnerH - gutter - 2 * layout.MetaH);
        DrawMetaStrip(px, w, layout, metaModules, ix, iy + layout.InnerH - gutter - layout.MetaH);

        // Data grid, row-blit style: paint only the first scanline of each cell row, then
        // memcpy that scanline down the remaining cellPx-1 rows — the fill becomes mostly
        // Array.Copy instead of per-pixel stores.
        int dataX = ix + layout.DataLeft, dataY = iy + layout.DataTop;
        int cell = layout.CellPx, bits = layout.BitsPerCell;
        long cellIndex = 0;
        for (int gy = 0; gy < layout.GridH; gy++)
        {
            int firstRow = (dataY + gy * cell) * w;
            for (int gx = 0; gx < layout.GridW; gx++, cellIndex++)
            {
                int v = bitStream.ReadCell(cellBuffer, cellIndex * bits, bits);
                Array.Fill(px, palette[v], firstRow + dataX + gx * cell, cell);
            }
            int rowStart = firstRow + dataX;
            int rowLen = layout.GridW * cell;
            for (int r = 1; r < cell; r++)
                Array.Copy(px, rowStart, px, rowStart + r * w, rowLen);
        }

        writer.Write(outPath, px, w, h);
    }

    /// <summary>
    /// Camera-profile finder bands: four 7-module concentric-square finder patterns at the
    /// image corners (row/column signature 1:1:3:1:1) plus the solid orientation tick
    /// 7 modules right of the top-left finder center. Geometry constants live in Layout and
    /// are shared with the camera rectifier.
    /// </summary>
    private static void DrawFinderBands(Rgb24[] px, int w, int h, Layout layout)
    {
        int m = layout.FinderModule;
        int inset = Layout.FinderCornerInsetModules * m;
        int finder = Layout.FinderModules * m;
        int bottomBandTop = h - layout.FinderBand;

        DrawFinder(px, w, inset, inset, m);                                    // top-left
        DrawFinder(px, w, w - inset - finder, inset, m);                       // top-right
        DrawFinder(px, w, inset, bottomBandTop + inset, m);                    // bottom-left
        DrawFinder(px, w, w - inset - finder, bottomBandTop + inset, m);       // bottom-right

        // Orientation tick: a 3-module solid square centered 7 modules right of the TL finder
        // center (center y = inset + 3.5m). Its mirrored position near the TR finder stays
        // white, which is what disambiguates the four rotations.
        int tickCenterX = inset + finder / 2 + Layout.OrientationTickOffsetModules * m;
        int tickCenterY = inset + finder / 2;
        FillRect(px, w, tickCenterX - (3 * m) / 2, tickCenterY - (3 * m) / 2, 3 * m, 3 * m, new Rgb24(0, 0, 0));
    }

    private static void DrawFinder(Rgb24[] px, int stride, int x, int y, int m)
    {
        var black = new Rgb24(0, 0, 0);
        var white = new Rgb24(255, 255, 255);
        FillRect(px, stride, x, y, 7 * m, 7 * m, black);
        FillRect(px, stride, x + m, y + m, 5 * m, 5 * m, white);
        FillRect(px, stride, x + 2 * m, y + 2 * m, 3 * m, 3 * m, black);
    }

    private static void DrawMetaStrip(Rgb24[] px, int stride, Layout layout, byte[] metaModules, int ix, int yTop)
    {
        var white = new Rgb24(255, 255, 255);
        var black = new Rgb24(0, 0, 0);
        int gutter = layout.Gutter;
        double moduleW = (layout.InnerW - 2.0 * gutter) / Layout.MetaModuleCount;
        for (int m = 0; m < Layout.MetaModuleCount; m++)
        {
            bool bit = (metaModules[m >> 3] & (0x80 >> (m & 7))) != 0;
            int x0 = ix + gutter + (int)Math.Round(m * moduleW);
            int x1 = ix + gutter + (int)Math.Round((m + 1) * moduleW);
            FillRect(px, stride, x0, yTop, x1 - x0, layout.MetaH, bit ? black : white);
        }
    }

    private static void DrawPaletteStrip(Rgb24[] px, int stride, Layout layout, Rgb24[] palette, int ix, int yTop)
    {
        int gutter = layout.Gutter;
        double blockW = (layout.InnerW - 2.0 * gutter) / palette.Length;
        for (int c = 0; c < palette.Length; c++)
        {
            int x0 = ix + gutter + (int)Math.Round(c * blockW);
            int x1 = ix + gutter + (int)Math.Round((c + 1) * blockW);
            FillRect(px, stride, x0, yTop, x1 - x0, layout.MetaH, palette[c]);
        }
    }

    private static void FillRect(Rgb24[] px, int stride, int x, int y, int rw, int rh, Rgb24 color)
    {
        if (rw <= 0 || rh <= 0)
            return;
        int first = y * stride + x;
        Array.Fill(px, color, first, rw);
        for (int yy = 1; yy < rh; yy++)
            Array.Copy(px, first, px, first + yy * stride, rw);
    }
}
