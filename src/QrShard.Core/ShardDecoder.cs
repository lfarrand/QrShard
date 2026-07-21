using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// The decode orchestrator: parallel folder decoding, per-image pipeline dispatch (axis-aligned
/// first, camera rectification as fallback), and hand-off to reassembly. The pipeline stages it
/// composes (frame location, strip reading, grid sampling, FEC, reassembly) are pure static
/// components; this class carries the injected configuration and camera dependency.
/// </summary>
internal sealed class ShardDecoder(
    AppSettings settings, ICameraRectifier cameraRectifier, IFrameLocator frameLocator,
    IStripReader stripReader, IGridSampler gridSampler, IShardAssembler assembler,
    Fec fec, Crc crc, FastPngReader pngReader, IPhotoFusion photoFusion, Interleaver2 interleaver) : IShardDecoder
{
    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardDecoder() : this(
        AppSettings.Current, new CameraRectifier(), new FrameLocator(new InnerRectScanner(), new StripReader()),
        new StripReader(), new GridSampler(), new ShardAssembler(),
        new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2())
    {
    }

    public List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log,
        string? password = null)
    {
        var shards = CollectShards(imagePaths, log);
        if (shards.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found.");
        return assembler.Assemble(shards, outputPath, log, password);
    }

    /// <summary>Decodes every image to shards without assembling — the building block for sessions and verify.</summary>
    public List<DecodedShard> CollectShards(IEnumerable<string> imagePaths, Action<string> log)
    {
        var ordered = imagePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var results = new (DecodedShard? Shard, string? Error)[ordered.Count];

        // One reusable scratch (pixel + visited buffers, the two large per-image allocations)
        // per worker: decoding N images then costs ~2 buffers per worker instead of 2N GC'd
        // arrays. PNG decode goes memory-bandwidth-bound past ~16 workers, hence the automatic
        // cap (overridable via appsettings.json DecodeMaxParallelism).
        int parallelism = settings.DecodeMaxParallelism;
        if (parallelism <= 0)
            parallelism = Math.Min(Environment.ProcessorCount, 16);
        var failures = new FailedCapture?[ordered.Count];
        Parallel.For(0, ordered.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            () => new DecodeScratch(),
            (i, _, scratch) =>
            {
                var diagnostics = new DecodeDiagnostics();
                try
                {
                    results[i] = (DecodeImage(ordered[i], scratch, diagnostics), null);
                }
                catch (ShardDecodeException ex)
                {
                    results[i] = (null, ex.Message);
                    if (diagnostics is { Layout: not null, Cells: not null })
                        failures[i] = new FailedCapture(diagnostics.Layout, diagnostics.Cells, ordered[i]);
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

        // Multi-capture fusion: several failed captures of the same shard may still combine
        // into a valid one (glare and reflections move between shots).
        var failed = failures.OfType<FailedCapture>().ToList();
        if (failed.Count >= 2)
        {
            foreach (var shard in photoFusion.Fuse(failed, log))
            {
                if (!shards.Any(s => s.Header.FileId == shard.Header.FileId &&
                                     s.Header.Index == shard.Header.Index &&
                                     s.Header.IsParity == shard.Header.IsParity))
                    shards.Add(shard);
            }
        }
        return shards;
    }

    public DecodedShard DecodeImage(string path) => DecodeImage(path, new DecodeScratch());

    /// <summary>Diagnostic single-image decode: captures the layout and per-codeword ECC
    /// statistics whether or not the decode succeeds, with the same camera-rectification
    /// fallback as the normal pipeline — so photo captures diagnose (and calibrate) too.</summary>
    public DecodeDiagnostics Diagnose(string path)
    {
        var scratch = new DecodeScratch();
        var diagnostics = new DecodeDiagnostics();
        try
        {
            Bitmap bmp = LoadBitmap(path, scratch);
            try
            {
                diagnostics.Shard = DecodeBitmap(bmp, scratch, path, diagnostics);
            }
            catch (ShardDecodeException)
            {
                Bitmap? rectified;
                try
                {
                    rectified = cameraRectifier.TryRectify(bmp);
                }
                catch (ShardDecodeException)
                {
                    rectified = null;
                }
                if (rectified is null)
                    throw;
                diagnostics.Shard = DecodeBitmap(rectified, scratch, path, diagnostics);
            }
        }
        catch (ShardDecodeException ex)
        {
            diagnostics.Error = ex.Message;
        }
        return diagnostics;
    }

    public DecodedShard DecodeImage(string path, DecodeScratch scratch) => DecodeImage(path, scratch, null);

    /// <summary>Decodes one image already in memory (encoded bytes), for callers that receive
    /// captures over a wire rather than as files — the incremental session path.</summary>
    public DecodedShard DecodeImageBytes(ReadOnlySpan<byte> imageBytes, DecodeScratch scratch, string label)
    {
        Image<Rgb24> image;
        try
        {
            image = Image.Load<Rgb24>(imageBytes);
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
        return DecodeBitmapWithCameraFallback(bmp, scratch, label);
    }

    /// <summary>Axis-aligned decode with the camera-rectification fallback, shared by the file
    /// and in-memory entry points.</summary>
    private DecodedShard DecodeBitmapWithCameraFallback(Bitmap bmp, DecodeScratch scratch, string label)
    {
        try
        {
            return DecodeBitmap(bmp, scratch, label, null);
        }
        catch (ShardDecodeException axisAlignedError)
        {
            Bitmap? rectified;
            try
            {
                rectified = cameraRectifier.TryRectify(bmp);
            }
            catch (ShardDecodeException)
            {
                rectified = null;
            }
            if (rectified is null)
                throw;
            try
            {
                return DecodeBitmap(rectified, scratch, label, null);
            }
            catch (ShardDecodeException cameraError)
            {
                throw new ShardDecodeException(
                    $"Camera-rectified decode failed: {cameraError.Message} (axis-aligned attempt: {axisAlignedError.Message})");
            }
        }
    }

    private DecodedShard DecodeImage(string path, DecodeScratch scratch, DecodeDiagnostics? diagnostics)
    {
        Bitmap bmp = LoadBitmap(path, scratch);

        try
        {
            return DecodeBitmap(bmp, scratch, path, diagnostics);
        }
        catch (ShardDecodeException axisAlignedError)
        {
            // Camera fallback: photos are rotated/perspective-distorted, which the axis-aligned
            // pipeline cannot handle. If the image carries camera-profile finder patterns,
            // rectify it into an axis-aligned canvas and run the same pipeline on that.
            Bitmap? rectified;
            try
            {
                rectified = cameraRectifier.TryRectify(bmp);
            }
            catch (ShardDecodeException)
            {
                rectified = null;
            }
            if (rectified is null)
                throw;

            try
            {
                return DecodeBitmap(rectified, scratch, path, diagnostics);
            }
            catch (ShardDecodeException cameraError)
            {
                throw new ShardDecodeException(
                    $"Camera-rectified decode failed: {cameraError.Message} (axis-aligned attempt: {axisAlignedError.Message})");
            }
        }
    }

    /// <summary>Reads a bitmap into the scratch's pooled pixel buffer, preferring the fast PNG
    /// reader and falling back to ImageSharp for anything outside its truecolor subset.</summary>
    private Bitmap LoadBitmap(string path, DecodeScratch scratch)
    {
        if (pngReader.TryRead(path, scratch, out Bitmap bmp))
            return bmp;

        Image<Rgb24> image;
        try
        {
            image = Image.Load<Rgb24>(path);
        }
        catch (ImageFormatException ex)
        {
            throw new ShardDecodeException($"Not a readable image ({ex.Message}).");
        }

        using (image)
        {
            var px = scratch.Pixels(image.Width * image.Height);
            image.CopyPixelDataTo(px.AsSpan(0, image.Width * image.Height));
            return new Bitmap(px, image.Width, image.Height);
        }
    }

    public DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path) =>
        DecodeBitmap(bmp, scratch, path, null);

    private DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path, DecodeDiagnostics? diagnostics)
    {
        var (layout, inner) = frameLocator.Locate(bmp, scratch);
        if (diagnostics is not null)
            diagnostics.Layout = layout;
        var palette = stripReader.ReadPalette(bmp, inner, layout);
        byte[] cells = gridSampler.ReadDataGrid(bmp, inner, layout, palette, scratch,
            out bool[]? suspectBytes, out byte[]? secondChoiceBytes);

        // v2 interleave: gather the permuted cell stream back into classic order so the whole
        // SIMD/erasure/Chase machinery — and multi-capture fusion — run unchanged.
        byte[] work = cells;
        bool[]? workSuspects = suspectBytes;
        byte[]? workSecond = secondChoiceBytes;
        int protectedLength = layout.CodewordCount * Fec.CodewordLength;
        if (layout.Interleave2 && layout.EccParity > 0)
        {
            var gathered = scratch.GatheredCells(protectedLength);
            interleaver.Gather(cells, gathered, protectedLength);
            work = gathered;
            if (suspectBytes is not null)
            {
                var flags = scratch.GatheredFlags(protectedLength);
                interleaver.GatherFlags(suspectBytes, flags, protectedLength);
                workSuspects = flags;
            }
            if (secondChoiceBytes is not null)
            {
                var second = scratch.GatheredSecond(protectedLength);
                interleaver.Gather(secondChoiceBytes, second, protectedLength);
                workSecond = second;
            }
        }

        // Copy the (classic-order) cells into the diagnostics on failure — the raw material
        // for multi-capture fusion. First failing attempt wins (scratch buffers are reused).
        void Salvage()
        {
            if (diagnostics is not null && diagnostics.Cells is null)
            {
                int salvageLength = layout.EccParity > 0 ? protectedLength : (int)((layout.TotalBits + 7) / 8);
                diagnostics.Cells = work.AsSpan(0, salvageLength).ToArray();
            }
        }

        byte[] stream;
        int correctedBytes = 0;
        if (layout.EccParity > 0)
        {
            stream = scratch.Recovered(layout.CodewordCount * Fec.DataLength(layout.EccParity));
            int[]? codewordErrors = diagnostics is not null ? new int[layout.CodewordCount] : null;
            bool recovered = fec.TryRecoverInto(work, layout.EccParity, layout.CodewordCount, stream, out correctedBytes, codewordErrors, workSuspects, workSecond);
            if (diagnostics is not null)
                diagnostics.CodewordErrors = codewordErrors!;
            if (!recovered)
            {
                Salvage();
                throw new ShardDecodeException("Damage exceeds the error-correction capacity of this image. Recapture it.");
            }
        }
        else
        {
            stream = cells;
        }

        var header = ShardHeader.Deserialize(stream, out int headerLen);
        if (header is null)
        {
            Salvage();
            throw new ShardDecodeException("Shard header is corrupt. Recapture this image.");
        }
        if ((header.Flags & ~ShardHeader.KnownFlags) != 0)
            throw new ShardDecodeException(
                $"This shard uses features from a newer QrShard (unknown flags 0x{header.Flags & ~ShardHeader.KnownFlags:X2}). Update QrShard to decode it.");
        if (headerLen + header.PayloadLength > stream.Length)
        {
            Salvage();
            throw new ShardDecodeException("Shard header declares more payload than the image holds.");
        }
        byte[] payload = stream[headerLen..(headerLen + header.PayloadLength)];
        if (crc.Crc32(payload) != header.PayloadCrc32)
        {
            Salvage();
            throw new ShardDecodeException($"Payload CRC-32 mismatch (part {header.Index + 1}/{header.Count}). Recapture this image.");
        }
        return new DecodedShard(header, payload, path, layout.EccParity, correctedBytes);
    }
}
