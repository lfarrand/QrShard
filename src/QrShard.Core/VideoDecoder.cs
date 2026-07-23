using SixLabors.ImageSharp;

namespace QrShard;

internal sealed record VideoDecodeStats(int FramesExamined, int FramesDecoded, int ShardsCollected, bool StoppedEarly);

/// <summary>
/// Decodes shards from a recording of the slideshow — the receiver-side half of video mode.
///
/// Frames come from the injected <see cref="IFrameSource"/> (ffmpeg pipe / ImageSharp by
/// default; a fake in unit tests). Two optimizations make hour-long recordings cheap:
///  - a tiny downsampled-luminance pre-filter skips frames nearly identical to the previous
///    one (a 30 fps recording of a 2 img/s slideshow is ~94% duplicates);
///  - decoding stops the moment the collected shard set is complete or recoverable via parity.
/// Torn mid-transition frames simply fail CRC/ECC and are skipped; the loop guarantees the
/// same shard comes around again.
/// </summary>
internal sealed class VideoDecoder(
    IShardDecoder decoder, IFrameSource frameSource, IShardAssembler assembler,
    IParityReassembler parityReassembler, ICameraRectifier cameraRectifier) : IVideoDecoder
{
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v"];

    /// <summary>Mean-abs-luminance difference (0-255) below which a frame is treated as a duplicate.</summary>
    private const double DuplicateThreshold = 3.0;

    /// <summary>Cap on frames folded into one temporal average — √32 ≈ 5.7x noise reduction is
    /// plenty, and it bounds the accumulation work per failed shard group.</summary>
    private const int MaxAveragedFrames = 32;

    /// <summary>Whether the recording shows the screen directly or through a camera.</summary>
    private enum CaptureMode
    {
        Unknown,
        AxisAligned,
        Camera,
    }

    /// <summary>Default wiring for tests and non-DI callers.</summary>
    public VideoDecoder() : this(new ShardDecoder(), new RecordingFrameSource(),
        new ShardAssembler(), new ParityReassembler(), new CameraRectifier())
    {
    }

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>An image file whose container holds more than one frame (APNG/GIF/WebP animation).</summary>
    public static bool IsAnimatedImage(string path)
    {
        try
        {
            var info = Image.Identify(path);
            return info.FrameMetadataCollection.Count > 1;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }

    public List<RestoredFile> Decode(string path, string? outputPath, double extractFps, Action<string> log,
        out VideoDecodeStats stats, string? password = null, int decodeWorkers = 1, bool escalateFps = false)
    {
        var shards = new List<DecodedShard>();
        var seen = new HashSet<(ulong FileId, int Index, bool Parity)>();
        int totalExamined = 0, totalDecoded = 0;
        bool stoppedEarly = false;

        // A re-extractable file source (not live capture) that decodes incomplete can be
        // re-run at a higher extraction rate — the transfer may cycle faster than the frames
        // we sampled. Passes accumulate into the shard set, stopping the moment it completes.
        double fps = extractFps;
        var ladder = escalateFps && IsVideoFile(path) ? new[] { fps, fps * 2, fps * 4 } : [fps];
        for (int pass = 0; pass < ladder.Length; pass++)
        {
            double passFps = ladder[pass];
            if (pass > 0)
                log($"  set still incomplete — re-extracting at {passFps} fps");
            int shardsBefore = shards.Count;
            var frames = frameSource.Frames(path, passFps);
            bool complete = decodeWorkers > 1
                ? CollectShardsParallel(frames, shards, seen, log, decodeWorkers, out var passStats)
                : CollectShards(frames, shards, seen, log, out passStats);
            totalExamined += passStats.FramesExamined;
            totalDecoded += passStats.FramesDecoded;
            stoppedEarly = passStats.StoppedEarly;
            if (complete)
                break;
            // A re-extraction pass samples the video more densely than the last, so if it added
            // no new shards the video's decodable content is saturated — a still-denser pass
            // cannot reveal shards that simply are not in it. Stop rather than re-demux again.
            if (pass > 0 && shards.Count == shardsBefore)
            {
                log("  higher-rate pass found no new shards — video is fully sampled, stopping");
                break;
            }
        }

        stats = new VideoDecodeStats(totalExamined, totalDecoded, shards.Count, stoppedEarly);
        log($"  video: examined {stats.FramesExamined} frame(s), fully decoded {stats.FramesDecoded}, " +
            $"collected {stats.ShardsCollected} shard(s){(stats.StoppedEarly ? ", stopped early — set complete" : "")}");
        if (shards.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found in the video.");
        return assembler.Assemble(shards, outputPath, log, password);
    }

    // ---------- Shard collection with dedupe + early stop ----------

    /// <summary>Collects into a caller-owned shard set (so escalation passes accumulate);
    /// returns true when the set became complete.</summary>
    private bool CollectShards(IEnumerable<Bitmap> frames, List<DecodedShard> shards,
        HashSet<(ulong FileId, int Index, bool Parity)> seen, Action<string> log, out VideoDecodeStats stats)
    {
        var scratch = new DecodeScratch();
        var signature = new byte[SignatureLength];
        var previousSignature = new byte[SignatureLength];
        bool hasPrevious = false;
        int examined = 0, decoded = 0;
        bool stoppedEarly = false;
        var mode = CaptureMode.Unknown;
        CameraPose? cachedPose = null;

        // Temporal averaging: a slideshow shows each shard across many near-duplicate frames, of
        // which only the first is normally decoded and the rest discarded. When NO single frame of
        // a group decodes, average the group's frames — independent sensor noise averages toward
        // the clean image (~1/sqrt(N)), pushing a sub-cliff shard over. The duplicate threshold
        // guarantees the frames are registered enough to average in pixel space. Only failed
        // groups accumulate, so a clean transfer pays nothing.
        int[]? sum = null;
        int avgW = 0, avgH = 0, avgCount = 0;
        bool groupYielded = false;

        foreach (var frame in frames)
        {
            examined++;
            FrameSignature(frame, signature);
            bool duplicate = hasPrevious && MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
            (previousSignature, signature) = (signature, previousSignature);
            hasPrevious = true;

            if (duplicate)
            {
                if (!groupYielded && sum is not null && avgCount is >= 1 and < MaxAveragedFrames)
                {
                    Accumulate(sum, frame);
                    avgCount++;
                }
                continue;
            }

            // Group boundary: fall back to a temporal-average decode of the group that just ended.
            if (!groupYielded && sum is not null && avgCount >= 2)
            {
                decoded++;
                if (TryCollect(BuildAverage(sum, avgW, avgH, avgCount), scratch, examined, ref mode, ref cachedPose,
                        shards, seen, log, $"averaged {avgCount} frames"))
                {
                    stoppedEarly = true;
                    break;
                }
            }

            // Primary path: decode this (first) frame of the new group.
            avgCount = 0;
            decoded++;
            bool complete = TryCollect(frame, scratch, examined, ref mode, ref cachedPose, shards, seen, log,
                $"frame {examined}", out groupYielded);
            if (complete)
            {
                stoppedEarly = true;
                break;
            }
            if (!groupYielded)
            {
                if (sum is null || avgW != frame.Width || avgH != frame.Height)
                {
                    avgW = frame.Width;
                    avgH = frame.Height;
                    sum = new int[avgW * avgH * 3];
                }
                else
                {
                    Array.Clear(sum);
                }
                Accumulate(sum, frame);
                avgCount = 1;
            }
        }

        // Flush the final group's average if it never decoded.
        if (!stoppedEarly && !groupYielded && sum is not null && avgCount >= 2)
        {
            decoded++;
            TryCollect(BuildAverage(sum, avgW, avgH, avgCount), scratch, examined, ref mode, ref cachedPose,
                shards, seen, log, $"averaged {avgCount} frames");
        }

        stats = new VideoDecodeStats(examined, decoded, shards.Count, stoppedEarly);
        return stoppedEarly || parityReassembler.IsSetComplete(shards);
    }

    /// <summary>Decodes one frame and collects its shard (deduplicated). Returns true when the set
    /// became complete; <paramref name="yielded"/> is true when the frame decoded at all (so its
    /// group needs no temporal-average retry).</summary>
    private bool TryCollect(Bitmap frame, DecodeScratch scratch, int examined, ref CaptureMode mode,
        ref CameraPose? cachedPose, List<DecodedShard> shards,
        HashSet<(ulong FileId, int Index, bool Parity)> seen, Action<string> log, string label, out bool yielded)
    {
        yielded = false;
        try
        {
            var shard = DecodeFrame(frame, scratch, examined, ref mode, ref cachedPose);
            yielded = true; // decoded to a shard (new or already-seen) — averaging this group is unnecessary
            if (!seen.Add((shard.Header.FileId, shard.Header.Index, shard.Header.IsParity)))
                return false;
            shards.Add(shard);
            string which = shard.Header.IsParity
                ? $"parity #{shard.Header.Index + 1}"
                : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
            log($"  ok      {label}  ({which}, {shard.Payload.Length:N0} bytes) — {shards.Count} collected");
            return parityReassembler.IsSetComplete(shards);
        }
        catch (ShardDecodeException)
        {
            return false; // torn/blended/junk frame — the loop brings the shard around again
        }
    }

    private bool TryCollect(Bitmap frame, DecodeScratch scratch, int examined, ref CaptureMode mode,
        ref CameraPose? cachedPose, List<DecodedShard> shards,
        HashSet<(ulong FileId, int Index, bool Parity)> seen, Action<string> log, string label)
        => TryCollect(frame, scratch, examined, ref mode, ref cachedPose, shards, seen, log, label, out _);

    internal static void Accumulate(int[] sum, Bitmap frame)
    {
        var px = frame.Px;
        for (int i = 0; i < px.Length; i++)
        {
            int j = i * 3;
            sum[j] += px[i].R;
            sum[j + 1] += px[i].G;
            sum[j + 2] += px[i].B;
        }
    }

    internal static Bitmap BuildAverage(int[] sum, int w, int h, int count)
    {
        var px = new SixLabors.ImageSharp.PixelFormats.Rgb24[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            int j = i * 3;
            px[i] = new SixLabors.ImageSharp.PixelFormats.Rgb24(
                (byte)(sum[j] / count), (byte)(sum[j + 1] / count), (byte)(sum[j + 2] / count));
        }
        return new Bitmap(px, w, h);
    }

    /// <summary>
    /// Pipelined variant for the live receiver: one producer reads and dedupes frames while
    /// several workers decode concurrently — per-frame decode latency is the throughput
    /// ceiling on camera-profile streams, so overlapping frames matters there. The bounded
    /// queue gives backpressure; completing the set cancels the producer, whose enumerator
    /// disposal kills ffmpeg. (File recordings keep the sequential path: its early-stop
    /// guarantees are exact, which the tests — and the "no wasted demux" promise — rely on.)
    /// </summary>
    private bool CollectShardsParallel(IEnumerable<Bitmap> frames, List<DecodedShard> shards,
        HashSet<(ulong FileId, int Index, bool Parity)> seen, Action<string> log, int workers,
        out VideoDecodeStats stats)
    {
        using var queue = new System.Collections.Concurrent.BlockingCollection<(Bitmap Frame, int Index)>(workers * 2);
        using var cts = new CancellationTokenSource();
        int examined = 0, decodedCount = 0;
        bool stoppedEarly = false;
        object gate = new();

        var producer = Task.Run(() =>
        {
            var signature = new byte[SignatureLength];
            var previousSignature = new byte[SignatureLength];
            bool hasPrevious = false;
            try
            {
                foreach (var frame in frames)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    int index = ++examined; // producer-only until the final barrier
                    FrameSignature(frame, signature);
                    bool duplicate = hasPrevious && MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
                    (previousSignature, signature) = (signature, previousSignature);
                    hasPrevious = true;
                    if (duplicate)
                        continue;
                    Interlocked.Increment(ref decodedCount);
                    try
                    {
                        queue.Add((frame, index), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        var workerTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            var scratch = new DecodeScratch();
            var mode = CaptureMode.Unknown; // per-worker latch/pose: benign duplication
            CameraPose? cachedPose = null;
            foreach (var (frame, index) in queue.GetConsumingEnumerable())
            {
                if (cts.IsCancellationRequested)
                    break;
                try
                {
                    var shard = DecodeFrame(frame, scratch, index, ref mode, ref cachedPose);
                    lock (gate)
                    {
                        if (!seen.Add((shard.Header.FileId, shard.Header.Index, shard.Header.IsParity)))
                            continue;
                        shards.Add(shard);
                        string which = shard.Header.IsParity
                            ? $"parity #{shard.Header.Index + 1}"
                            : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
                        log($"  ok      frame {index}  ({which}, {shard.Payload.Length:N0} bytes) — {shards.Count} collected");
                        if (parityReassembler.IsSetComplete(shards))
                        {
                            stoppedEarly = true;
                            cts.Cancel();
                        }
                    }
                }
                catch (ShardDecodeException)
                {
                    // torn or non-shard frame — the stream will bring the shard around again
                }
            }
        })).ToArray();

        Task.WaitAll(workerTasks);
        producer.GetAwaiter().GetResult(); // unwrap: surface the producer's ShardDecodeException, not an AggregateException
        stats = new VideoDecodeStats(examined, decodedCount, shards.Count, stoppedEarly);
        return stoppedEarly || parityReassembler.IsSetComplete(shards);
    }

    /// <summary>
    /// Per-frame decode with a capture-mode latch: once frames prove to be direct screen
    /// recordings, camera detection never runs; once they prove to be camera footage, the
    /// axis-aligned attempt is skipped and the detected pose is CACHED — consecutive frames of
    /// a handheld recording share nearly the same pose, and phase-2 refinement absorbs the
    /// drift, so full finder detection only reruns when a cached pose stops decoding.
    /// </summary>
    private DecodedShard DecodeFrame(Bitmap frame, DecodeScratch scratch, int examined,
        ref CaptureMode mode, ref CameraPose? cachedPose)
    {
        if (mode == CaptureMode.Camera)
            return DecodeCameraFrame(frame, scratch, examined, ref cachedPose);

        try
        {
            var shard = decoder.DecodeBitmap(frame, scratch, $"frame {examined}");
            mode = CaptureMode.AxisAligned;
            return shard;
        }
        catch (ShardDecodeException) when (mode == CaptureMode.Unknown)
        {
            var shard = DecodeCameraFrame(frame, scratch, examined, ref cachedPose);
            mode = CaptureMode.Camera; // only reached when the camera path succeeded
            return shard;
        }
    }

    private DecodedShard DecodeCameraFrame(Bitmap frame, DecodeScratch scratch, int examined, ref CameraPose? cachedPose)
    {
        if (cachedPose is not null)
        {
            try
            {
                return decoder.DecodeBitmap(cameraRectifier.RectifyWithPose(frame, cachedPose), scratch, $"frame {examined}");
            }
            catch (ShardDecodeException)
            {
                cachedPose = null; // drifted too far — fall through to full detection
            }
        }

        // Sharpness gate: full finder detection + rectification is the most expensive per-frame
        // work, and a motion-blurred handheld frame cannot decode anyway. A cheap high-frequency
        // energy check rejects the blurriest frames before that work — the transfer cycles, so a
        // sharp capture of the same shard comes around again.
        if (FocusEnergy(frame) < BlurRejectThreshold)
            throw new ShardDecodeException("Frame too blurred to attempt rectification.");

        var pose = cameraRectifier.DetectPose(frame)
            ?? throw new ShardDecodeException("No finder patterns in this frame.");
        var shard = decoder.DecodeBitmap(cameraRectifier.RectifyWithPose(frame, pose), scratch, $"frame {examined}");
        cachedPose = pose; // latch only after a successful decode
        return shard;
    }

    /// <summary>Mean squared horizontal-gradient over a sampled grid — a cheap focus proxy. A
    /// sharp shard (hard cell edges) has high gradient energy; motion blur smears it toward 0.</summary>
    internal const long BlurRejectThreshold = 40;

    internal static long FocusEnergy(Bitmap frame)
    {
        const int grid = 48;
        int w = frame.Width, h = frame.Height;
        if (w < 4)
            return long.MaxValue; // too small to gate meaningfully — never reject
        long sum = 0;
        int samples = 0;
        for (int gy = 0; gy < grid; gy++)
        {
            int y = (2 * gy + 1) * h / (2 * grid);
            for (int gx = 0; gx < grid; gx++)
            {
                int x = Math.Min(w - 2, (2 * gx + 1) * w / (2 * grid));
                var a = frame.At(x, y);
                var b = frame.At(x + 1, y);
                int d = (a.R + a.G + a.B) / 3 - (b.R + b.G + b.B) / 3;
                sum += (long)d * d;
                samples++;
            }
        }
        return samples == 0 ? long.MaxValue : sum / samples;
    }

    private const int SignatureGrid = 32;
    private const int SignatureLength = SignatureGrid * SignatureGrid;

    /// <summary>
    /// Sparse point-sample signature for cheap near-duplicate rejection: the luminance of 1024
    /// exact pixels on a fixed grid, written into a caller-reused buffer. Point samples,
    /// deliberately not averages — shard content is noise-like, so any downsampled average
    /// converges to the same mean for every shard, while exact pixels differ almost everywhere
    /// between different shards.
    /// </summary>
    private static void FrameSignature(Bitmap frame, byte[] signature)
    {
        for (int gy = 0; gy < SignatureGrid; gy++)
        {
            int y = (2 * gy + 1) * frame.Height / (2 * SignatureGrid);
            for (int gx = 0; gx < SignatureGrid; gx++)
            {
                int x = (2 * gx + 1) * frame.Width / (2 * SignatureGrid);
                var p = frame.At(x, y);
                signature[gy * SignatureGrid + gx] = (byte)((p.R + p.G + p.B) / 3);
            }
        }
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
