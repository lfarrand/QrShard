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
    Fec fec, Crc crc, FastPngReader pngReader) : IShardDecoder
{
    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardDecoder() : this(
        AppSettings.Current, new CameraRectifier(), new FrameLocator(new InnerRectScanner(), new StripReader()),
        new StripReader(), new GridSampler(), new ShardAssembler(),
        new Fec(), new Crc(), new FastPngReader())
    {
    }

    public List<RestoredFile> DecodeFolder(IEnumerable<string> imagePaths, string? outputPath, Action<string> log,
        string? password = null)
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
        Parallel.For(0, ordered.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
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

        return assembler.Assemble(shards, outputPath, log, password);
    }

    public DecodedShard DecodeImage(string path) => DecodeImage(path, new DecodeScratch());

    public DecodedShard DecodeImage(string path, DecodeScratch scratch)
    {
        // Fast path: our own truecolor-PNG reader. Anything it can't handle (other formats,
        // palette/16-bit/interlaced PNGs, corrupt files) falls through to ImageSharp.
        if (!pngReader.TryRead(path, scratch, out Bitmap bmp))
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

            using (image)
            {
                var px = scratch.Pixels(image.Width * image.Height);
                image.CopyPixelDataTo(px.AsSpan(0, image.Width * image.Height));
                bmp = new Bitmap(px, image.Width, image.Height);
            }
        }

        try
        {
            return DecodeBitmap(bmp, scratch, path);
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
                return DecodeBitmap(rectified, scratch, path);
            }
            catch (ShardDecodeException cameraError)
            {
                throw new ShardDecodeException(
                    $"Camera-rectified decode failed: {cameraError.Message} (axis-aligned attempt: {axisAlignedError.Message})");
            }
        }
    }

    public DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path)
    {
        var (layout, inner) = frameLocator.Locate(bmp, scratch);
        var palette = stripReader.ReadPalette(bmp, inner, layout);
        byte[] cells = gridSampler.ReadDataGrid(bmp, inner, layout, palette, scratch);

        byte[] stream;
        int correctedBytes = 0;
        if (layout.EccParity > 0)
        {
            stream = scratch.Recovered(layout.CodewordCount * Fec.DataLength(layout.EccParity));
            if (!fec.TryRecoverInto(cells, layout.EccParity, layout.CodewordCount, stream, out correctedBytes))
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
        if (crc.Crc32(payload) != header.PayloadCrc32)
            throw new ShardDecodeException($"Payload CRC-32 mismatch (part {header.Index + 1}/{header.Count}). Recapture this image.");
        return new DecodedShard(header, payload, path, layout.EccParity, correctedBytes);
    }
}
