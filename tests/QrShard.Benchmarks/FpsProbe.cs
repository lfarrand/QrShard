using System.Diagnostics;
using System.Globalization;

namespace QrShard.Benchmarks;

/// <summary>
/// Measures the decoder's frame rate — how fast captured images turn back into shards on the
/// receiving side — across a selection of resolutions/densities. For each it reports single-core
/// per-frame latency, the aggregate throughput of the parallel decoder (its real ceiling on this
/// machine), the resulting payload bandwidth, and the codec-bound time for a 250 MB transfer.
///
/// This is the decode ceiling, not an end-to-end transfer estimate: real transfers are usually
/// bound by how fast frames can be displayed and captured, which the README covers separately.
/// </summary>
internal static class FpsProbe
{
    // Decimal MB, to match the "Payload/image" figures in the README capacity table this feeds.
    private const double MB = 1000.0 * 1000.0;

    private sealed record Config(string Name, EncodeOptions Options);

    public static void Run(TextWriter output)
    {
        var configs = new[]
        {
            new Config("Default  2160x2160 3px 4bit", new EncodeOptions()),
            new Config("Dense    2160x2160 2px 6bit", new EncodeOptions { CellPx = 2, BitsPerCell = 6 }),
            new Config("Max4K    3840x2160 1px 6bit", new EncodeOptions { Width = 3840, Height = 2160, CellPx = 1, BitsPerCell = 6 }),
            new Config("Max4K-8  3840x2160 1px 8bit", new EncodeOptions { Width = 3840, Height = 2160, CellPx = 1, BitsPerCell = 8 }),
        };

        int workers = Math.Min(Environment.ProcessorCount, 16);
        output.WriteLine($"decode workers (auto cap): {workers} of {Environment.ProcessorCount} logical cores");
        output.WriteLine("single = one image at a time on one core; parallel = the default multi-worker decode");
        output.WriteLine();
        output.WriteLine(
            $"{"config",-28} {"payload/img",12} {"single ms",10} {"single fps",11} " +
            $"{"par fps",8} {"par MB/s",9} {"250 MB",9}");
        output.WriteLine(new string('-', 92));

        string root = Path.Combine(Path.GetTempPath(), $"qrshard-fps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var cfg in configs)
                MeasureOne(output, root, cfg);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best effort — temp dir */ }
        }

        output.WriteLine();
        output.WriteLine("MB = 1,000,000 bytes (matches the capacity table); 250 MB time = codec-bound (parallel decode only).");
    }

    private static void MeasureOne(TextWriter output, string root, Config cfg)
    {
        var opt = cfg.Options;
        long capacity =
            Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity).UsableBytes
            - ShardHeader.Size(BenchPresets.PayloadName);

        // Enough distinct images to saturate the worker pool a few times over, so the parallel
        // number reflects steady state rather than ramp-up on a handful of frames.
        const int imageCount = 32;
        string input = Path.Combine(root, "payload.bin");
        WriteRandom(input, capacity * imageCount);

        string shardDir = Path.Combine(root, "shards");
        new ShardEncoder().Encode(input, shardDir, opt);
        var images = Directory.GetFiles(shardDir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToList();

        var decoder = new ShardDecoder();

        // Single-core per-frame latency: one reused scratch, images decoded sequentially.
        var scratch = new DecodeScratch();
        foreach (var p in images.Take(4)) decoder.DecodeImage(p, scratch); // warm + JIT
        var sw = Stopwatch.StartNew();
        int frames = 0;
        while (sw.ElapsedMilliseconds < 1500)
            foreach (var p in images) { decoder.DecodeImage(p, scratch); frames++; }
        sw.Stop();
        double singleMs = sw.Elapsed.TotalMilliseconds / frames;
        double singleFps = 1000.0 / singleMs;

        // Aggregate throughput: the real parallel decode path (CollectShards), run repeatedly.
        decoder.CollectShards(images, _ => { }); // warm
        var sw2 = Stopwatch.StartNew();
        int aggFrames = 0;
        while (sw2.ElapsedMilliseconds < 1500)
        {
            decoder.CollectShards(images, _ => { });
            aggFrames += images.Count;
        }
        sw2.Stop();
        double parFps = aggFrames / sw2.Elapsed.TotalSeconds;
        double parMbPerSec = parFps * capacity / MB;
        double secs250 = 250.0 * MB / (parMbPerSec * MB); // = 250 / parMbPerSec, in decimal MB

        Directory.Delete(shardDir, recursive: true);
        File.Delete(input);

        output.WriteLine(
            $"{cfg.Name,-28} {FormatBytes(capacity),10}   {singleMs,8:0.0}  {singleFps,9:0.0}  " +
            $"{parFps,7:0}  {parMbPerSec,7:0}  {FormatSeconds(secs250),8}");
    }

    private static void WriteRandom(string path, long size)
    {
        var rng = new Random(1234);
        var buffer = new byte[8 * 1024 * 1024];
        long remaining = size;
        using var fs = File.Create(path);
        while (remaining > 0)
        {
            int n = (int)Math.Min(buffer.Length, remaining);
            rng.NextBytes(buffer.AsSpan(0, n));
            fs.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= MB
            ? $"{(bytes / MB).ToString("0.0", CultureInfo.InvariantCulture)} MB"
            : $"{(bytes / 1000.0).ToString("0", CultureInfo.InvariantCulture)} KB";

    private static string FormatSeconds(double s) => s switch
    {
        < 60 => $"{s.ToString("0.0", CultureInfo.InvariantCulture)} s",
        < 3600 => $"{(s / 60).ToString("0.0", CultureInfo.InvariantCulture)} min",
        _ => $"{(s / 3600).ToString("0.0", CultureInfo.InvariantCulture)} h",
    };
}
