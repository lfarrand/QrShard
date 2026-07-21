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
        out VideoDecodeStats stats, string? password = null, int decodeWorkers = 1)
    {
        var frames = frameSource.Frames(path, extractFps);
        var shards = decodeWorkers > 1
            ? CollectShardsParallel(frames, log, decodeWorkers, out stats)
            : CollectShards(frames, log, out stats);
        log($"  video: examined {stats.FramesExamined} frame(s), fully decoded {stats.FramesDecoded}, " +
            $"collected {stats.ShardsCollected} shard(s){(stats.StoppedEarly ? ", stopped early — set complete" : "")}");
        if (shards.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found in the video.");
        return assembler.Assemble(shards, outputPath, log, password);
    }

    // ---------- Shard collection with dedupe + early stop ----------

    private List<DecodedShard> CollectShards(IEnumerable<Bitmap> frames, Action<string> log, out VideoDecodeStats stats)
    {
        var scratch = new DecodeScratch();
        var shards = new List<DecodedShard>();
        var seen = new HashSet<(ulong FileId, int Index, bool Parity)>();
        byte[]? previousSignature = null;
        int examined = 0, decoded = 0;
        bool stoppedEarly = false;
        var mode = CaptureMode.Unknown;
        CameraPose? cachedPose = null;

        foreach (var frame in frames)
        {
            examined++;
            byte[] signature = FrameSignature(frame);
            bool duplicate = previousSignature is not null && MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
            previousSignature = signature;
            if (duplicate)
                continue;

            decoded++;
            try
            {
                var shard = DecodeFrame(frame, scratch, examined, ref mode, ref cachedPose);
                if (seen.Add((shard.Header.FileId, shard.Header.Index, shard.Header.IsParity)))
                {
                    shards.Add(shard);
                    string which = shard.Header.IsParity
                        ? $"parity #{shard.Header.Index + 1}"
                        : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
                    log($"  ok      frame {examined}  ({which}, {shard.Payload.Length:N0} bytes) — {shards.Count} collected");

                    if (parityReassembler.IsSetComplete(shards))
                    {
                        stoppedEarly = true;
                        break;
                    }
                }
            }
            catch (ShardDecodeException)
            {
                // Torn/blended transition frame, or junk between shards — the loop will bring
                // the shard around again, so silently skip.
            }
        }

        stats = new VideoDecodeStats(examined, decoded, shards.Count, stoppedEarly);
        return shards;
    }

    /// <summary>
    /// Pipelined variant for the live receiver: one producer reads and dedupes frames while
    /// several workers decode concurrently — per-frame decode latency is the throughput
    /// ceiling on camera-profile streams, so overlapping frames matters there. The bounded
    /// queue gives backpressure; completing the set cancels the producer, whose enumerator
    /// disposal kills ffmpeg. (File recordings keep the sequential path: its early-stop
    /// guarantees are exact, which the tests — and the "no wasted demux" promise — rely on.)
    /// </summary>
    private List<DecodedShard> CollectShardsParallel(IEnumerable<Bitmap> frames, Action<string> log, int workers,
        out VideoDecodeStats stats)
    {
        var shards = new List<DecodedShard>();
        var seen = new HashSet<(ulong FileId, int Index, bool Parity)>();
        using var queue = new System.Collections.Concurrent.BlockingCollection<(Bitmap Frame, int Index)>(workers * 2);
        using var cts = new CancellationTokenSource();
        int examined = 0, decodedCount = 0;
        bool stoppedEarly = false;
        object gate = new();

        var producer = Task.Run(() =>
        {
            byte[]? previousSignature = null;
            try
            {
                foreach (var frame in frames)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    int index = ++examined; // producer-only until the final barrier
                    byte[] signature = FrameSignature(frame);
                    bool duplicate = previousSignature is not null && MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
                    previousSignature = signature;
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
        producer.Wait();
        stats = new VideoDecodeStats(examined, decodedCount, shards.Count, stoppedEarly);
        return shards;
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

        var pose = cameraRectifier.DetectPose(frame)
            ?? throw new ShardDecodeException("No finder patterns in this frame.");
        var shard = decoder.DecodeBitmap(cameraRectifier.RectifyWithPose(frame, pose), scratch, $"frame {examined}");
        cachedPose = pose; // latch only after a successful decode
        return shard;
    }

    /// <summary>
    /// Sparse point-sample signature for cheap near-duplicate rejection: the luminance of 1024
    /// exact pixels on a fixed grid. Point samples, deliberately not averages — shard content
    /// is noise-like, so any downsampled average converges to the same mean for every shard,
    /// while exact pixels differ almost everywhere between different shards.
    /// </summary>
    private static byte[] FrameSignature(Bitmap frame)
    {
        const int grid = 32;
        var signature = new byte[grid * grid];
        for (int gy = 0; gy < grid; gy++)
        {
            int y = (2 * gy + 1) * frame.Height / (2 * grid);
            for (int gx = 0; gx < grid; gx++)
            {
                int x = (2 * gx + 1) * frame.Width / (2 * grid);
                var p = frame.At(x, y);
                signature[gy * grid + gx] = (byte)((p.R + p.G + p.B) / 3);
            }
        }
        return signature;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
