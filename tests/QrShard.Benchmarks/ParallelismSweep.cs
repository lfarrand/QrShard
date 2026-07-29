using System.Diagnostics;
using System.Globalization;

namespace QrShard.Benchmarks;

/// <summary>
/// Sweeps <c>DecodeMaxParallelism</c> across a range of worker counts, to check the automatic cap
/// in <see cref="ShardDecoder.CollectShards"/> against the machine actually running it. Also
/// reports the PNG-decode share of a single-threaded decode, since "PNG decode saturates memory
/// bandwidth" was the cap's original (measured-false) justification.
///
/// Two things make a naive sweep of this lie, both learned the hard way:
///
/// 1. Pick an image count divisible by EVERY worker count under test (96 suits 8/12/16/24/32).
///    Parallel.For hands out chunks, so a ragged split leaves workers idle in the tail — worth
///    up to 40% here. Sweeping 128 images makes 16 and 32 workers look like peaks and 20 and 24
///    like troughs; sweeping 120 reverses that verdict entirely. That curve is measuring
///    arithmetic, not hardware.
/// 2. Sample round-robin, alternating direction, and report the median. A straight A-then-B
///    sweep is swamped by boost/thermal drift and background load; interleaving cancels it
///    because every worker count sees the same drift.
/// </summary>
internal static class ParallelismSweep
{
    private const double MB = 1000.0 * 1000.0;

    public static void Run(TextWriter output, string presetName, int[] workerCounts, int samples, int imageCount)
    {
        // This measurement compares thread-pool saturation levels, so preemption by unrelated
        // desktop load is not just noise — it biases the wider pools. Ask for priority.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { }

        var opt = BenchPresets.Options(presetName);
        long capacity =
            Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity).UsableBytes
            - ShardHeader.Size(BenchPresets.PayloadName);

        string root = Path.Combine(Path.GetTempPath(), $"qrshard-parsweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, BenchPresets.PayloadName);
            WriteRandom(input, capacity * imageCount);
            string shardDir = Path.Combine(root, "shards");
            new ShardEncoder().Encode(input, shardDir, opt);
            var images = Directory.GetFiles(shardDir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToList();
            File.Delete(input);

            output.WriteLine($"preset {presetName} ({opt.Width}x{opt.Height} {opt.CellPx}px {opt.BitsPerCell}bit), " +
                             $"{images.Count} images, {capacity / MB:0.00} MB payload each");
            output.WriteLine($"machine: {Environment.ProcessorCount} logical cores");
            output.WriteLine();

            ComponentSplit(output, images);

            var decoders = workerCounts.ToDictionary(n => n, n => Build(root, n));
            var times = workerCounts.ToDictionary(n => n, _ => new List<double>());

            foreach (var n in workerCounts) decoders[n].CollectShards(images, _ => { }); // warm all paths

            // Round-robin so drift hits every worker count equally; direction alternates so a
            // monotone drift within one rep cannot favour whichever count is measured first.
            for (int rep = 0; rep < samples; rep++)
            {
                var order = rep % 2 == 0 ? workerCounts : workerCounts.Reverse().ToArray();
                foreach (var n in order)
                {
                    var sw = Stopwatch.StartNew();
                    var shards = decoders[n].CollectShards(images, _ => { });
                    sw.Stop();
                    if (shards.Count != images.Count)
                        throw new InvalidOperationException($"decoded {shards.Count} of {images.Count} shards");
                    times[n].Add(sw.Elapsed.TotalSeconds);
                }
            }

            // Background load can only ever slow a sample down, so the fastest sample is the
            // least-contaminated estimate of uncontended throughput; the median is reported too
            // because it is what a user on a busy machine actually experiences.
            // Percentages are relative to the shipped automatic cap, so the table reads directly
            // as "what changing the cap would buy" on the machine it is running on.
            int reference = workerCounts.Contains(ShardDecoder.AutoParallelism)
                ? ShardDecoder.AutoParallelism
                : workerCounts[0];
            output.WriteLine($"{"workers",8} {"med fps",9} {"best fps",9} {"best MB/s",10} " +
                             $"{"med vs" + reference,9} {"best vs" + reference,10}");
            output.WriteLine(new string('-', 62));
            double medRef = Median(times[reference].Select(t => images.Count / t).ToList());
            double bestRef = images.Count / times[reference].Min();
            foreach (var n in workerCounts)
            {
                var fps = times[n].Select(t => images.Count / t).ToList();
                double med = Median(fps);
                double best = fps.Max();
                output.WriteLine(
                    $"{n,8} {med,9:0.0} {best,9:0.0} {best * capacity / MB,10:0} " +
                    $"{(med / medRef - 1) * 100,8:+0.0;-0.0;0.0}% {(best / bestRef - 1) * 100,9:+0.0;-0.0;0.0}%");
            }
            output.WriteLine();
            foreach (var n in workerCounts)
                output.WriteLine($"  raw fps w={n,3}: " +
                    string.Join(" ", times[n].Select(t => (images.Count / t).ToString("0", CultureInfo.InvariantCulture))));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best effort — temp dir */ }
        }
    }

    /// <summary>
    /// Throughput, memory and CPU for ONE (workers, images) pair, all from the same run, so the
    /// figures describe a single configuration rather than three separate ones stitched together.
    ///
    /// Run one pair per process. Peak working set and allocation both carry over within a process
    /// — a larger image count measured earlier leaves its peak behind, and the GC state it left
    /// changes what the next configuration allocates — so looping counts in-process would report
    /// the history of the run rather than the configuration.
    ///
    /// CPU is total processor time across the sampled region divided by that region's wall clock,
    /// which gives cores actually kept busy. That is the number that distinguishes "faster" from
    /// "burning more of the machine to go the same speed", and it is the whole question when
    /// comparing a chunked partitioner against a work-stealing one.
    /// </summary>
    public static void Compare(TextWriter output, string presetName, int workers, int images, int samples)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException) { }

        var opt = BenchPresets.Options(presetName);
        long capacity =
            Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity).UsableBytes
            - ShardHeader.Size(BenchPresets.PayloadName);

        string root = Path.Combine(Path.GetTempPath(), $"qrshard-parcmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, BenchPresets.PayloadName);
            WriteRandom(input, capacity * images);
            string shardDir = Path.Combine(root, "shards");
            new ShardEncoder().Encode(input, shardDir, opt);
            var files = Directory.GetFiles(shardDir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToList();
            File.Delete(input);

            var decoder = Build(root, workers);
            decoder.CollectShards(files, _ => { }); // warm: JIT, file cache, pool threads

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var self = Process.GetCurrentProcess();
            self.Refresh();
            long baseWs = self.WorkingSet64;

            long peakWs = baseWs;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                var p = Process.GetCurrentProcess();
                while (!stop.IsCancellationRequested)
                {
                    p.Refresh();
                    long ws = p.WorkingSet64;
                    if (ws > peakWs) peakWs = ws;
                    Thread.Sleep(10);
                }
            });

            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            TimeSpan cpuBefore = self.TotalProcessorTime;
            var regionSw = Stopwatch.StartNew();

            var times = new List<double>(samples);
            for (int rep = 0; rep < samples; rep++)
            {
                var sw = Stopwatch.StartNew();
                var shards = decoder.CollectShards(files, _ => { });
                sw.Stop();
                if (shards.Count != files.Count)
                    throw new InvalidOperationException($"decoded {shards.Count} of {files.Count} shards");
                times.Add(sw.Elapsed.TotalSeconds);
            }

            regionSw.Stop();
            self.Refresh();
            TimeSpan cpuAfter = self.TotalProcessorTime;
            long alloc = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            stop.Cancel();
            sampler.Wait();

            var fps = times.Select(t => images / t).ToList();
            double medFps = Median(fps);
            double bestFps = fps.Max();
            // Sampler wall time includes the gaps between passes; there are none here beyond the
            // loop itself, so this is the region's true occupancy.
            double cpuCores = (cpuAfter - cpuBefore).TotalSeconds / regionSw.Elapsed.TotalSeconds;

            output.WriteLine(
                $"RESULT images={images} workers={workers} " +
                $"medFps={medFps:0.0} bestFps={bestFps:0.0} MBps={medFps * capacity / MB:0} " +
                $"peakWsMB={(peakWs - baseWs) / (1024.0 * 1024):0} allocPassMB={alloc / (double)samples / (1024 * 1024):0} " +
                $"cpuCores={cpuCores:0.00} cpuPct={cpuCores / Environment.ProcessorCount * 100:0.0}");
            output.WriteLine($"  raw fps: {string.Join(" ", fps.Select(f => f.ToString("0", CultureInfo.InvariantCulture)))}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best effort — temp dir */ }
        }
    }

    /// <summary>Peak working set added by one decode pass at a single worker count, measured from
    /// a post-encode baseline. Run one worker count per process for an uncontaminated figure.</summary>
    public static void Memory(TextWriter output, string presetName, int workers, int imageCount)
    {
        var opt = BenchPresets.Options(presetName);
        long capacity =
            Layout.Create(opt.Width, opt.Height, opt.CellPx, opt.BitsPerCell, opt.EccParity).UsableBytes
            - ShardHeader.Size(BenchPresets.PayloadName);
        string root = Path.Combine(Path.GetTempPath(), $"qrshard-parmem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, BenchPresets.PayloadName);
            WriteRandom(input, capacity * imageCount);
            string shardDir = Path.Combine(root, "shards");
            new ShardEncoder().Encode(input, shardDir, opt);
            var images = Directory.GetFiles(shardDir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToList();
            File.Delete(input);

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var self = Process.GetCurrentProcess();
            self.Refresh();
            long before = self.WorkingSet64;

            long peak = before;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                var p = Process.GetCurrentProcess();
                while (!stop.IsCancellationRequested)
                {
                    p.Refresh();
                    long ws = p.WorkingSet64;
                    if (ws > peak) peak = ws;
                    Thread.Sleep(10);
                }
            });

            var decoder = Build(root, workers);
            long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
            decoder.CollectShards(images, _ => { });
            decoder.CollectShards(images, _ => { });
            long alloc = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
            stop.Cancel();
            sampler.Wait();

            output.WriteLine(
                $"{presetName,-10} workers={workers,3}  baseWS={before / (1024.0 * 1024),7:0} MB  " +
                $"peakWS={peak / (1024.0 * 1024),7:0} MB  decodeDelta={(peak - before) / (1024.0 * 1024),7:0} MB  " +
                $"alloc/pass={alloc / 2.0 / (1024 * 1024),7:0} MB");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* best effort — temp dir */ }
        }
    }

    /// <summary>How much of a single-threaded decode is PNG read versus everything downstream of
    /// it — the share that has to be large before "PNG decode saturates memory bandwidth" can
    /// explain a scaling ceiling. It is not: 6-9% at 4K, ~25% at the default density.</summary>
    private static void ComponentSplit(TextWriter output, List<string> images)
    {
        var scratch = new DecodeScratch();
        var decoder = new ShardDecoder();
        var pngReader = new FastPngReader();
        foreach (var p in images.Take(4)) { decoder.DecodeImage(p, scratch); pngReader.TryRead(p, scratch, out _); }

        var swAll = Stopwatch.StartNew();
        int n = 0;
        while (swAll.ElapsedMilliseconds < 1200)
            foreach (var p in images) { decoder.DecodeImage(p, scratch); n++; }
        swAll.Stop();
        double fullMs = swAll.Elapsed.TotalMilliseconds / n;

        var swPng = Stopwatch.StartNew();
        int m = 0;
        while (swPng.ElapsedMilliseconds < 1200)
            foreach (var p in images) { pngReader.TryRead(p, scratch, out _); m++; }
        swPng.Stop();
        double pngMs = swPng.Elapsed.TotalMilliseconds / m;

        output.WriteLine($"single-thread per image: {fullMs,6:0.0} ms total, {pngMs,5:0.0} ms PNG read " +
                         $"({pngMs / fullMs * 100:0.0}% of decode)");
        output.WriteLine();
    }

    private static ShardDecoder Build(string root, int parallelism)
    {
        // AppSettings has no public mutators by design (it is a parsed file); write the one
        // setting under test to a scratch file and load it the way the CLI would.
        string path = Path.Combine(root, $"appsettings-{parallelism}.json");
        File.WriteAllText(path, $"{{ \"DecodeMaxParallelism\": {parallelism} }}");
        var settings = AppSettings.Load(path);
        return new ShardDecoder(settings, new CameraRectifier(),
            new FrameLocator(new InnerRectScanner(), new StripReader()), new StripReader(), new GridSampler(),
            new ShardAssembler(), new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2());
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
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

    public static int[] ParseCounts(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
}
