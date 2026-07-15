using System.IO.Compression;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

internal sealed record DecodedShard(ShardHeader Header, byte[] Payload, string SourceFile, int EccParity, int CorrectedBytes);

internal sealed record RestoredFile(string FileName, string OutputPath, long Length);

internal static class Decoder
{
    private const int DarkThreshold = 80; // luminance below this is "frame black"

    // ---------- Whole-folder decode ----------

    public static List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log)
    {
        var ordered = imagePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var results = new (DecodedShard? Shard, string? Error)[ordered.Count];

        // One reusable scratch (pixel + visited buffers, the two large per-image allocations)
        // per worker: decoding N images then costs ~2 buffers per worker instead of 2N GC'd arrays.
        Parallel.For(0, ordered.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 16) },
            () => new DecodeScratch(),
            (i, _, scratch) =>
            {
                try
                {
                    results[i] = (DecodeImage(ordered[i], scratch), null);
                }
                catch (ShardDecodeException ex)
                {
                    results[i] = (null, ex.Message);
                }
                return scratch;
            },
            _ => { });

        var shards = new List<DecodedShard>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (shard, error) = results[i];
            if (shard is not null)
            {
                shards.Add(shard);
                string corrections = shard.CorrectedBytes > 0 ? $", ECC corrected {shard.CorrectedBytes} bytes" : "";
                string which = shard.Header.IsParity
                    ? $"parity #{shard.Header.Index + 1}"
                    : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
                log($"  ok      {Path.GetFileName(ordered[i])}  ({which}, {shard.Payload.Length:N0} bytes{corrections})");
            }
            else
            {
                log($"  FAILED  {Path.GetFileName(ordered[i])}: {error}");
            }
        }

        if (shards.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found.");

        var groups = shards.GroupBy(s => s.Header.FileId).ToList();
        if (outputPath is not null && groups.Count > 1)
            throw new ShardDecodeException("The images belong to multiple different files; omit -o or decode them separately.");

        var restored = new List<RestoredFile>();
        foreach (var group in groups)
            restored.Add(Reassemble([.. group], outputPath, log));
        return restored;
    }

    private static RestoredFile Reassemble(List<DecodedShard> shards, string? outputPath, Action<string> log)
    {
        var first = shards[0].Header;
        int count = first.Count;
        foreach (var s in shards)
            if (s.Header.Count != count)
                throw new ShardDecodeException($"Inconsistent shard set for '{first.FileName}': image counts differ.");

        // Both reassembly paths allocate the exact output size up front, so bound it first.
        if (first.TotalLength is < 0 or > Encoder.MaxFileBytes || first.OriginalLength is < 0 or > Encoder.MaxFileBytes)
            throw new ShardDecodeException($"'{first.FileName}': shard header declares an implausible file size.");

        byte[] data = first.StripeParity > 0
            ? ReassembleWithParity(shards, first, log)
            : ReassembleContiguous(shards, first);

        if ((first.Flags & ShardHeader.FlagCompressed) != 0)
        {
            try
            {
                data = Inflate(data, (int)first.OriginalLength);
            }
            catch (InvalidDataException)
            {
                throw new ShardDecodeException($"'{first.FileName}': the reassembled stream failed to decompress. A shard is corrupt beyond recovery.");
            }
        }

        byte[] sha = SHA256.HashData(data);
        if (!sha.AsSpan().SequenceEqual(first.Sha256))
            throw new ShardDecodeException($"'{first.FileName}': SHA-256 of the reassembled file does not match the original. A shard was corrupted.");

        string outPath = outputPath ?? Path.Combine(Environment.CurrentDirectory, first.FileName);
        if (outputPath is null && File.Exists(outPath))
            outPath = Path.Combine(Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(first.FileName)}.restored{Path.GetExtension(first.FileName)}");
        File.WriteAllBytes(outPath, data);
        log($"  SHA-256 verified ✓  '{first.FileName}' → {outPath} ({data.Length:N0} bytes)");
        return new RestoredFile(first.FileName, outPath, data.LongLength);
    }

    /// <summary>Original path: no cross-shard parity — every data image must be present.</summary>
    private static byte[] ReassembleContiguous(List<DecodedShard> shards, ShardHeader first)
    {
        var byIndex = new DecodedShard?[first.Count];
        foreach (var s in shards)
            if (!s.Header.IsParity)
                byIndex[s.Header.Index] ??= s;

        var missing = Enumerable.Range(0, first.Count).Where(i => byIndex[i] is null).ToList();
        if (missing.Count > 0)
            throw new ShardDecodeException(
                $"'{first.FileName}': missing image(s) {string.Join(", ", missing.Select(i => i + 1))} of {first.Count}. " +
                "Capture them and decode again.");

        if (byIndex.Sum(s => (long)s!.Payload.Length) != first.TotalLength)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

        var data = new byte[first.TotalLength];
        int offset = 0;
        foreach (var s in byIndex)
        {
            s!.Payload.CopyTo(data.AsSpan(offset));
            offset += s.Payload.Length;
        }
        return data;
    }

    /// <summary>
    /// Cross-shard-parity path: reconstructs any missing data images from parity images,
    /// stripe by stripe. Tolerates losing up to StripeParity images per stripe.
    /// </summary>
    private static byte[] ReassembleWithParity(List<DecodedShard> shards, ShardHeader first, Action<string> log)
    {
        int count = first.Count, s = first.StripeData, p = first.StripeParity;
        int stripes = (count + s - 1) / s;
        int cap = shards.Max(x => x.Payload.Length); // full per-image capacity (parity images are always full)

        var dataByIndex = new DecodedShard?[count];
        var parityByOrdinal = new DecodedShard?[stripes * p];
        foreach (var x in shards)
        {
            if (x.Header.IsParity)
            {
                if (x.Header.Index < parityByOrdinal.Length)
                    parityByOrdinal[x.Header.Index] ??= x;
            }
            else
            {
                dataByIndex[x.Header.Index] ??= x;
            }
        }

        var chunks = new byte[count][];
        var unrecoverable = new List<int>();
        int reconstructed = 0;

        for (int g = 0; g < stripes; g++)
        {
            int first0 = g * s;
            int sData = Math.Min(s, count - first0);
            var present = new byte[]?[sData + p];
            int have = 0;

            for (int t = 0; t < sData; t++)
            {
                var shard = dataByIndex[first0 + t];
                if (shard is not null)
                {
                    present[t] = Pad(shard.Payload, cap);
                    have++;
                }
            }
            for (int pi = 0; pi < p; pi++)
            {
                var shard = parityByOrdinal[g * p + pi];
                if (shard is not null)
                {
                    present[sData + pi] = shard.Payload; // already full length
                    have++;
                }
            }

            bool allDataPresent = Enumerable.Range(0, sData).All(t => present[t] is not null);
            if (allDataPresent)
            {
                for (int t = 0; t < sData; t++)
                    chunks[first0 + t] = present[t]!;
                continue;
            }

            if (have < sData || !CrossShardFec.TryReconstruct(present, sData, p, cap, out byte[][] recovered))
            {
                for (int t = 0; t < sData; t++)
                    if (dataByIndex[first0 + t] is null)
                        unrecoverable.Add(first0 + t);
                continue;
            }

            for (int t = 0; t < sData; t++)
            {
                chunks[first0 + t] = recovered[t];
                if (dataByIndex[first0 + t] is null)
                    reconstructed++;
            }
        }

        if (unrecoverable.Count > 0)
            throw new ShardDecodeException(
                $"'{first.FileName}': {unrecoverable.Count} data image(s) are missing and beyond parity recovery " +
                $"(images {string.Join(", ", unrecoverable.Take(10).Select(i => i + 1))}{(unrecoverable.Count > 10 ? ", ..." : "")} of {count}). " +
                "Capture more of the missing images and decode again.");

        if (reconstructed > 0)
            log($"  recovered {reconstructed} missing image(s) from parity");

        long lastLen = first.TotalLength - (long)(count - 1) * cap;
        if (lastLen < 0 || lastLen > cap)
            throw new ShardDecodeException($"'{first.FileName}': reassembled length does not match expected {first.TotalLength:N0}.");

        var data = new byte[first.TotalLength]; // offsets fit int: TotalLength <= Encoder.MaxFileBytes
        for (int i = 0; i < count; i++)
        {
            int len = i < count - 1 ? cap : (int)lastLen;
            chunks[i].AsSpan(0, Math.Min(len, chunks[i].Length)).CopyTo(data.AsSpan(i * cap));
        }
        return data;
    }

    private static byte[] Pad(byte[] src, int length)
    {
        if (src.Length == length)
            return src;
        var padded = new byte[length];
        Array.Copy(src, padded, Math.Min(src.Length, length));
        return padded;
    }

    // ---------- Single-image decode ----------

    /// <summary>Reusable per-worker buffers for the large per-image allocations.</summary>
    internal sealed class DecodeScratch
    {
        private Rgb24[]? _pixels;
        private bool[]? _visited;
        private byte[]? _cells;
        private byte[]? _recovered;
        private int[]? _lut;

        public Rgb24[] Pixels(int length)
        {
            if (_pixels is null || _pixels.Length < length)
                _pixels = new Rgb24[length];
            return _pixels;
        }

        public bool[] ClearedVisited(int length)
        {
            if (_visited is null || _visited.Length < length)
                _visited = new bool[length];
            else
                Array.Clear(_visited, 0, length);
            return _visited;
        }

        public byte[] ClearedCells(int length)
        {
            if (_cells is null || _cells.Length < length)
                _cells = new byte[length];
            else
                Array.Clear(_cells, 0, length); // the grid reader ORs bits in
            return _cells;
        }

        public byte[] Recovered(int length)
        {
            if (_recovered is null || _recovered.Length < length)
                _recovered = new byte[length];
            return _recovered;
        }

        public int[] ResetNearestColorLut()
        {
            _lut ??= new int[1 << 15];
            Array.Fill(_lut, -1);
            return _lut;
        }
    }

    public static DecodedShard DecodeImage(string path) => DecodeImage(path, new DecodeScratch());

    internal static DecodedShard DecodeImage(string path, DecodeScratch scratch)
    {
        Image<Rgb24> image;
        try
        {
            image = Image.Load<Rgb24>(path);
        }
        catch (ImageFormatException ex)
        {
            throw new ShardDecodeException($"Not a readable image ({ex.Message}).");
        }

        Bitmap bmp;
        using (image)
        {
            var px = scratch.Pixels(image.Width * image.Height);
            image.CopyPixelDataTo(px.AsSpan(0, image.Width * image.Height));
            bmp = new Bitmap(px, image.Width, image.Height);
        }

        // Several dark rings can plausibly be the locator frame (e.g. a dark desktop border around
        // the capture also forms a ring); try candidates largest-first until the metadata validates.
        var candidates = FindFrameCandidates(bmp, scratch.ClearedVisited(bmp.Width * bmp.Height));
        if (candidates.Count == 0)
            throw new ShardDecodeException("Could not locate the black frame. Crop the screenshot to the code (keep some white margin) and try again.");

        Layout? layout = null;
        InnerRect inner = default;
        foreach (var frame in candidates.Take(8))
        {
            InnerRect ir;
            try
            {
                ir = FindInnerRect(bmp, frame);
            }
            catch (ShardDecodeException)
            {
                continue;
            }
            var l = ReadMetadata(bmp, ir);
            if (l is not null)
            {
                layout = l;
                inner = ir;
                break;
            }
        }
        if (layout is null)
            throw new ShardDecodeException("Found a frame but the metadata strip is unreadable (CRC mismatch). The capture may be scaled too small or blurred.");

        var palette = ReadPalette(bmp, inner, layout);
        byte[] cells = ReadDataGrid(bmp, inner, layout, palette, scratch);

        byte[] stream;
        int correctedBytes = 0;
        if (layout.EccParity > 0)
        {
            stream = scratch.Recovered(layout.CodewordCount * Fec.DataLength(layout.EccParity));
            if (!Fec.TryRecoverInto(cells, layout.EccParity, layout.CodewordCount, stream, out correctedBytes))
                throw new ShardDecodeException("Damage exceeds the error-correction capacity of this image. Recapture it.");
        }
        else
        {
            stream = cells;
        }

        var header = ShardHeader.Deserialize(stream, out int headerLen) ?? throw new ShardDecodeException("Shard header is corrupt. Recapture this image.");
        if (headerLen + header.PayloadLength > stream.Length)
            throw new ShardDecodeException("Shard header declares more payload than the image holds.");
        byte[] payload = stream[headerLen..(headerLen + header.PayloadLength)];
        if (Crc.Crc32(payload) != header.PayloadCrc32)
            throw new ShardDecodeException($"Payload CRC-32 mismatch (part {header.Index + 1}/{header.Count}). Recapture this image.");
        return new DecodedShard(header, payload, path, layout.EccParity, correctedBytes);
    }

    // ---------- Frame location ----------

    private readonly record struct Rect(int X0, int Y0, int X1, int Y1) // half-open
    {
        public int W => X1 - X0;
        public int H => Y1 - Y0;
    }

    /// <summary>Subpixel-accurate inner rectangle (the white area enclosed by the frame).</summary>
    private readonly record struct InnerRect(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0;
        public double H => Y1 - Y0;
    }

    private sealed class Bitmap(Rgb24[] px, int width, int height)
    {
        public readonly Rgb24[] Px = px;
        public readonly int Width = width;
        public readonly int Height = height;

        public Rgb24 At(int x, int y) => Px[y * Width + x];

        public bool IsDark(int x, int y)
        {
            var p = Px[y * Width + x];
            return p.R + p.G + p.B < DarkThreshold * 3;
        }
    }

    /// <summary>
    /// Finds locator-frame candidates: connected dark components whose bounding box is roughly
    /// square, ring-shaped (low fill density), and covers its own bounding-box edges.
    /// Returned largest-first; the caller validates each against the metadata strip.
    /// </summary>
    private static List<Rect> FindFrameCandidates(Bitmap bmp, bool[] visited)
    {
        int w = bmp.Width, h = bmp.Height;
        var stack = new Stack<int>();
        var candidates = new List<Rect>();

        for (int sy = 0; sy < h; sy += 2) // stride-2 seed scan; components are re-walked fully
        {
            for (int sx = 0; sx < w; sx += 2)
            {
                int seed = sy * w + sx;
                if (visited[seed] || !bmp.IsDark(sx, sy))
                    continue;

                int minX = sx, maxX = sx, minY = sy, maxY = sy;
                long pixels = 0;
                stack.Push(seed);
                visited[seed] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int x = p % w, y = p / w;
                    pixels++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    Visit(x - 1, y);
                    Visit(x + 1, y);
                    Visit(x, y - 1);
                    Visit(x, y + 1);
                }

                var box = new Rect(minX, minY, maxX + 1, maxY + 1);
                long area = (long)box.W * box.H;
                if (box.W < 100 || box.H < 100)
                    continue;
                double aspect = (double)box.W / box.H;
                double density = (double)pixels / area;
                if (aspect is < 0.3 or > 3.4 || density is < 0.005 or > 0.6)
                    continue;
                if (EdgeCoverage(bmp, box) < 0.85)
                    continue;
                candidates.Add(box);

                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        return;
                    int np = ny * w + nx;
                    if (!visited[np] && bmp.IsDark(nx, ny))
                    {
                        visited[np] = true;
                        stack.Push(np);
                    }
                }
            }
        }
        return [.. candidates.OrderByDescending(c => (long)c.W * c.H)];
    }

    /// <summary>Fraction of the bounding-box perimeter that is dark (a frame ring covers ~all of it).</summary>
    private static double EdgeCoverage(Bitmap bmp, Rect box)
    {
        long dark = 0, total = 0;
        for (int x = box.X0; x < box.X1; x++)
        {
            total += 2;
            if (bmp.IsDark(x, box.Y0)) dark++;
            if (bmp.IsDark(x, box.Y1 - 1)) dark++;
        }
        for (int y = box.Y0; y < box.Y1; y++)
        {
            total += 2;
            if (bmp.IsDark(box.X0, y)) dark++;
            if (bmp.IsDark(box.X1 - 1, y)) dark++;
        }
        return (double)dark / total;
    }

    /// <summary>
    /// Scans inward from the frame's outer box to its inner (white) edge with subpixel precision:
    /// the dark-to-light luminance crossing is linearly interpolated, and the median over five
    /// scan lines per side rejects outliers. Precision here directly bounds far-edge cell drift.
    /// </summary>
    private static InnerRect FindInnerRect(Bitmap bmp, Rect frame)
    {
        int[] ys = Enumerable.Range(0, 5).Select(i => frame.Y0 + frame.H * (3 + i) / 10).ToArray();
        int[] xs = Enumerable.Range(0, 5).Select(i => frame.X0 + frame.W * (3 + i) / 10).ToArray();

        double x0 = Median(ys.Select(y => EdgeX(frame.X0, +1, y)));
        double x1 = Median(ys.Select(y => EdgeX(frame.X1 - 1, -1, y)));
        double y0 = Median(xs.Select(x => EdgeY(frame.Y0, +1, x)));
        double y1 = Median(xs.Select(x => EdgeY(frame.Y1 - 1, -1, x)));
        if (x1 - x0 < 32 || y1 - y0 < 32)
            throw new ShardDecodeException("Frame interior is too small to decode.");
        return new InnerRect(x0, y0, x1, y1);

        double EdgeX(int start, int dir, int y) =>
            Edge(start, dir, Math.Abs(frame.W), i => Lum(bmp.At(i, y)));

        double EdgeY(int start, int dir, int x) =>
            Edge(start, dir, Math.Abs(frame.H), i => Lum(bmp.At(x, i)));

        // Walks from inside the frame toward the interior until luminance crosses 128,
        // then interpolates the crossing. Returns the subpixel edge coordinate.
        double Edge(int start, int dir, int limit, Func<int, double> lum)
        {
            int i = start;
            for (int steps = 0; steps < limit; steps++, i += dir)
            {
                double l = lum(i + dir);
                if (l >= 128)
                {
                    double lPrev = lum(i);
                    double frac = l > lPrev ? Math.Clamp((128 - lPrev) / (l - lPrev), 0, 1) : 0.5;
                    // Edge lies between pixel centers i and i+dir; convert to a boundary coordinate.
                    return dir > 0 ? i + 0.5 + frac : i + 0.5 - frac;
                }
            }
            throw new ShardDecodeException("Could not find the frame's inner edge.");
        }

        static double Lum(Rgb24 p) => (p.R + p.G + p.B) / 3.0;

        static double Median(IEnumerable<double> values)
        {
            var v = values.Order().ToArray();
            return v[v.Length / 2];
        }
    }

    // ---------- Strip + grid reading ----------

    private static Layout? ReadMetadata(Bitmap bmp, InnerRect inner)
    {
        // Before the metadata is read, gutter and strip height are only known via the shared
        // approximation innerWidth/100 (see Layout.EstimateMetaH); sampling mid-strip tolerates the ~1px error.
        double gutter = inner.W / 100.0;
        double metaH = Math.Max(6.0, inner.W / 100.0);

        // Redundant strips: top first, then the mirrored bottom copy.
        return TryReadStrip(inner.Y0 + gutter + metaH / 2)
            ?? TryReadStrip(inner.Y1 - gutter - metaH / 2);

        Layout? TryReadStrip(double yCenter)
        {
            double stripW = inner.W - 2 * gutter;
            double moduleW = stripW / Layout.MetaModuleCount;
            var modules = new bool[Layout.MetaModuleCount];
            for (int m = 0; m < Layout.MetaModuleCount; m++)
            {
                double xCenter = inner.X0 + gutter + (m + 0.5) * moduleW;
                var c = SampleBox(bmp, xCenter, yCenter, 1, 1);
                modules[m] = c.R + c.G + c.B < 128 * 3;
            }
            return Layout.UnpackMetadata(modules);
        }
    }

    private static Rgb24[] ReadPalette(Bitmap bmp, InnerRect inner, Layout layout)
    {
        // Two calibration strips exist (top and bottom). Measure both and keep the one whose
        // colors track the theoretical palette best — an overlay across one strip then costs nothing.
        var theoretical = Palette.Build(layout.BitsPerCell);
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
            measured[c] = SampleBox(bmp, inner.X0 + xEnc * sx, inner.Y0 + yEnc * sy, rx, ry);
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

    private static byte[] ReadDataGrid(Bitmap bmp, InnerRect inner, Layout layout, Rgb24[] palette, DecodeScratch scratch)
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
                    var c = SampleBox(bmp, x + dx, y + dy, 0, 0);
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

    /// <summary>
    /// Average color over a (2rx+1) x (2ry+1) pixel box around the (continuous, boundary-convention)
    /// position; the containing pixel is floor(coordinate).
    /// </summary>
    private static Rgb24 SampleBox(Bitmap bmp, double cx, double cy, int rx, int ry)
    {
        int x0 = Math.Clamp((int)Math.Floor(cx) - rx, 0, bmp.Width - 1);
        int x1 = Math.Clamp((int)Math.Floor(cx) + rx, 0, bmp.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(cy) - ry, 0, bmp.Height - 1);
        int y1 = Math.Clamp((int)Math.Floor(cy) + ry, 0, bmp.Height - 1);
        long r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var p = bmp.At(x, y);
                r += p.R;
                g += p.G;
                b += p.B;
                n++;
            }
        }
        return new Rgb24((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        // The original length is known from the (CRC-validated) header, so decompress straight
        // into an exact-size buffer — no MemoryStream doubling, no final ToArray copy. Any
        // length lie is caught by the SHA-256 verification that follows.
        using var input = new MemoryStream(data);
        using var ds = new DeflateStream(input, CompressionMode.Decompress);
        var result = new byte[expectedLength];
        int offset = 0;
        while (offset < result.Length)
        {
            int n = ds.Read(result, offset, result.Length - offset);
            if (n == 0)
                break;
            offset += n;
        }
        return result;
    }
}

internal sealed class ShardDecodeException(string message) : Exception(message);
