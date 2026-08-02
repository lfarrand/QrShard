using System.Runtime.ExceptionServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

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
    IParityReassembler parityReassembler, ICameraRectifier cameraRectifier, AppSettings settings) : IVideoDecoder
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
        new ShardAssembler(), new ParityReassembler(), new CameraRectifier(), AppSettings.BuiltIn)
    {
    }

    public VideoDecoder(IShardDecoder decoder, IFrameSource frameSource, IShardAssembler assembler,
        IParityReassembler parityReassembler, ICameraRectifier cameraRectifier)
        : this(decoder, frameSource, assembler, parityReassembler, cameraRectifier, AppSettings.BuiltIn)
    {
    }

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>An image file whose container holds more than one frame (APNG/GIF/WebP animation).</summary>
    public static bool IsAnimatedImage(string path)
    {
        try
        {
            // Dispatch needs to distinguish one frame from more than one, not inventory an
            // attacker-controlled animation with millions of frame records.
            var info = Image.Identify(new DecoderOptions { MaxFrames = 2, SkipMetadata = true }, path);
            return info.FrameCount > 1;
        }
        // The fourth Image.Identify/Image.Load site, and the one the previous two rounds walked
        // past. Cli dispatch calls this on the raw path BEFORE any decoder runs, so it sits
        // outside every per-image net in ShardDecoder: a PNG with a malformed zTXt chunk raises
        // System.IO.InvalidDataException here and `qrshard decode <that file>` died with a stack
        // trace, while `info` and `verify` on the same file reported it cleanly. Same policy as
        // the other three: a file this cannot identify is simply not an animation, and the decode
        // pass that follows diagnoses it properly.
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    public List<RestoredFile> Decode(string path, string? outputPath, double extractFps, Action<string> log,
        out VideoDecodeStats stats, string? password = null, int decodeWorkers = 1, bool escalateFps = false)
    {
        var shards = new List<DecodedShard>();
        var seen = new Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?>();
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(settings.DecodeMemoryBudgetMB);
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
            bool complete = decodeWorkers > 1
                ? CollectShardsParallel(token => frameSource.Frames(path, passFps, token),
                    shards, seen, successful, log, decodeWorkers, out var passStats)
                : CollectShards(frameSource.Frames(path, passFps), shards, seen, successful, log, out passStats);
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
        Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?> seen,
        ShardDecoder.SuccessfulShardRetentionBudget successful,
        Action<string> log, out VideoDecodeStats stats)
    {
        var scratch = new DecodeScratch();
        var signature = new byte[SignatureLength];
        var previousSignature = new byte[SignatureLength];
        bool hasPrevious = false;
        int previousWidth = 0, previousHeight = 0;
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
        bool averageBudgetWarningLogged = false;

        foreach (var frame in frames)
        {
            examined++;
            FrameSignature(frame, signature);
            bool duplicate = hasPrevious && frame.Width == previousWidth && frame.Height == previousHeight &&
                             MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
            (previousSignature, signature) = (signature, previousSignature);
            previousWidth = frame.Width;
            previousHeight = frame.Height;
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
                        shards, seen, successful, log, $"averaged {avgCount} frames"))
                {
                    stoppedEarly = true;
                    break;
                }
            }

            // Primary path: decode this (first) frame of the new group.
            avgCount = 0;
            decoded++;
            bool complete = TryCollect(frame, scratch, examined, ref mode, ref cachedPose, shards, seen, successful, log,
                $"frame {examined}", out groupYielded);
            if (complete)
            {
                stoppedEarly = true;
                break;
            }
            if (!groupYielded)
            {
                if (!CanTemporalAverage(frame, settings.DecodeMemoryBudgetMB))
                {
                    sum = null;
                    avgCount = 0;
                    if (!averageBudgetWarningLogged)
                    {
                        log($"  temporal averaging disabled for {frame.Width:N0}x{frame.Height:N0} frames: " +
                            $"its accumulator would exceed DecodeMemoryBudgetMB={settings.DecodeMemoryBudgetMB:N0}.");
                        averageBudgetWarningLogged = true;
                    }
                }
                else
                {
                    int required = checked(frame.Width * frame.Height * 3);
                    long retainedBytes = sum is not null && sum.Length < required
                        ? checked(sum.LongLength * sizeof(int))
                        : 0;
                    if (retainedBytes > 0 &&
                        !CanTemporalAverage(frame, settings.DecodeMemoryBudgetMB, retainedBytes))
                    {
                        // A changing-resolution stream can otherwise hold the old LOH accumulator
                        // while allocating a larger one. Skip this group; the now-unreferenced old
                        // buffer can be collected before a later group starts at the new size.
                        sum = null;
                        avgCount = 0;
                        if (!averageBudgetWarningLogged)
                        {
                            log("  temporal averaging skipped during a frame-size change: " +
                                $"overlapping accumulators would exceed DecodeMemoryBudgetMB={settings.DecodeMemoryBudgetMB:N0}.");
                            averageBudgetWarningLogged = true;
                        }
                    }
                    else
                    {
                        if (sum is null || sum.Length < required)
                            sum = new int[required];
                        else
                            Array.Clear(sum, 0, required);
                        avgW = frame.Width;
                        avgH = frame.Height;
                        Accumulate(sum, frame);
                        avgCount = 1;
                    }
                }
            }
        }

        // Flush the final group's average if it never decoded.
        if (!stoppedEarly && !groupYielded && sum is not null && avgCount >= 2)
        {
            decoded++;
            TryCollect(BuildAverage(sum, avgW, avgH, avgCount), scratch, examined, ref mode, ref cachedPose,
                shards, seen, successful, log, $"averaged {avgCount} frames");
        }

        stats = new VideoDecodeStats(examined, decoded, shards.Count, stoppedEarly);
        return stoppedEarly || parityReassembler.IsSetComplete(shards);
    }

    /// <summary>Decodes one frame and collects its shard (deduplicated). Returns true when the set
    /// became complete; <paramref name="yielded"/> is true when the frame decoded at all (so its
    /// group needs no temporal-average retry).</summary>
    private bool TryCollect(Bitmap frame, DecodeScratch scratch, int examined, ref CaptureMode mode,
        ref CameraPose? cachedPose, List<DecodedShard> shards,
        Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?> seen,
        ShardDecoder.SuccessfulShardRetentionBudget successful,
        Action<string> log, string label, out bool yielded)
    {
        yielded = false;
        try
        {
            var shard = DecodeFrame(frame, scratch, examined, ref mode, ref cachedPose);
            SuccessfulShardAdmission retention = successful.TryAdmitOwned(shard);
            if (retention.Kind == SuccessfulShardAdmissionKind.InconsistentFamily)
                throw successful.FamilyMismatchException();
            if (retention.Kind == SuccessfulShardAdmissionKind.Refused)
                throw successful.LimitException();
            yielded = true; // decoded to a shard (new or already-seen) — averaging this group is unnecessary
            if (retention.Kind is SuccessfulShardAdmissionKind.Duplicate or
                SuccessfulShardAdmissionKind.TerminalConflict)
                return false;
            CandidateAdmission admission = AdmitCandidate(shards, seen, shard);
            if (admission != CandidateAdmission.Added)
            {
                if (admission == CandidateAdmission.Conflict)
                {
                    successful.ReleaseAppliedConflict(shard.Header);
                    log($"  conflict {label}  (ordinal {(long)shard.Header.Index + 1} is now an erasure)");
                }
                return false;
            }
            successful.MarkReturnedExternal([shard]);
            string which = shard.Header.IsParity
                ? $"parity #{(long)shard.Header.Index + 1}"
                : $"part {(long)shard.Header.Index + 1}/{shard.Header.Count}";
            log($"  ok      {label}  ({which}, {shard.Payload.Length:N0} bytes) — {shards.Count} collected");
            return parityReassembler.IsSetComplete(shards);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException
                                   and not ShardResourceLimitException and not ShardFamilyMismatchException)
        {
            // Every frame is untrusted input. A malformed/torn frame must not abort a whole
            // recording merely because it reached an unexpected decoder exception; the folder
            // decoder uses the same isolation policy. Resource exhaustion and cancellation remain
            // process/run-level conditions and deliberately escape.
            return false;
        }
    }

    private bool TryCollect(Bitmap frame, DecodeScratch scratch, int examined, ref CaptureMode mode,
        ref CameraPose? cachedPose, List<DecodedShard> shards,
        Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?> seen,
        ShardDecoder.SuccessfulShardRetentionBudget successful, Action<string> log, string label)
        => TryCollect(frame, scratch, examined, ref mode, ref cachedPose, shards, seen, successful, log, label, out _);

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
        int pixels = checked(w * h);
        if (count < 1 || sum.Length < checked(pixels * 3))
            throw new ArgumentException("Temporal-average buffer does not match the frame geometry.");
        var px = new SixLabors.ImageSharp.PixelFormats.Rgb24[pixels];
        for (int i = 0; i < px.Length; i++)
        {
            int j = i * 3;
            px[i] = new SixLabors.ImageSharp.PixelFormats.Rgb24(
                (byte)(sum[j] / count), (byte)(sum[j + 1] / count), (byte)(sum[j + 2] / count));
        }
        return new Bitmap(px, w, h);
    }

    /// <summary>Whether one frame, the decode scratch, the RGB accumulators and the averaged
    /// output fit the configured planning budget. If not, single-frame decoding still proceeds.</summary>
    internal static bool CanTemporalAverage(Bitmap frame, int budgetMB)
        => CanTemporalAverage(frame, budgetMB, retainedBytes: 0);

    private static bool CanTemporalAverage(Bitmap frame, int budgetMB, long retainedBytes)
    {
        try
        {
            const int AccumulatorBytesPerPixel = 12; // three int channels
            const int AverageOutputBytesPerPixel = 3;
            long planned = checked((long)frame.Width * frame.Height *
                (ShardDecoder.ScratchBytesPerPixel + AccumulatorBytesPerPixel + AverageOutputBytesPerPixel) +
                retainedBytes);
            return planned <= checked(budgetMB * 1_000_000L);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pipelined variant for the live receiver: one producer reads and dedupes frames while
    /// several workers decode concurrently — per-frame decode latency is the throughput
    /// ceiling on camera-profile streams, so overlapping frames matters there. The bounded
    /// queue gives backpressure; completing the set cancels the producer, whose enumerator
    /// disposal kills ffmpeg. (File recordings keep the sequential path: its early-stop
    /// guarantees are exact, which the tests — and the "no wasted demux" promise — rely on.)
    /// </summary>
    private bool CollectShardsParallel(Func<CancellationToken, IEnumerable<Bitmap>> frameFactory,
        List<DecodedShard> shards,
        Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?> seen,
        ShardDecoder.SuccessfulShardRetentionBudget successful,
        Action<string> log, int workers,
        out VideoDecodeStats stats)
    {
        using var cts = new CancellationTokenSource();
        IEnumerable<Bitmap> frames = frameFactory(cts.Token);
        using IEnumerator<Bitmap> enumerator = frames.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            stats = new VideoDecodeStats(0, 0, shards.Count, false);
            return parityReassembler.IsSetComplete(shards);
        }

        Bitmap firstFrame = enumerator.Current;
        ShardDecoder.ValidateImageDimensions(firstFrame.Width, firstFrame.Height, settings.DecodeMemoryBudgetMB);
        long plannedPixels = checked((long)firstFrame.Width * firstFrame.Height);
        long perWorker = checked(plannedPixels * ShardDecoder.ScratchBytesPerPixel);
        int affordable = BudgetedLiveWorkers(firstFrame, workers, settings.DecodeMemoryBudgetMB);
        if (affordable < workers)
            log($"  using {affordable} live decode worker(s) instead of {workers}: " +
                $"{firstFrame.Width:N0}x{firstFrame.Height:N0} frames plan ~{perWorker / 1_000_000:N0} MB each " +
                $"against DecodeMemoryBudgetMB={settings.DecodeMemoryBudgetMB:N0}.");
        workers = affordable;

        IEnumerable<Bitmap> StableFrames()
        {
            yield return firstFrame;
            while (enumerator.MoveNext())
            {
                Bitmap frame = enumerator.Current;
                long pixels = checked((long)frame.Width * frame.Height);
                if (pixels > plannedPixels)
                    throw new ShardDecodeException(
                        $"Live frame dimensions increased from {firstFrame.Width:N0}x{firstFrame.Height:N0} to " +
                        $"{frame.Width:N0}x{frame.Height:N0} after worker memory planning; restart the receiver.");
                yield return frame;
            }
        }

        if (workers == 1)
            return CollectShards(StableFrames(), shards, seen, successful, log, out stats);

        // One pending frame is enough to overlap capture with decode and prevents a 64-worker
        // configuration from retaining another 128 full RGB frames outside the worker estimate.
        using var queue = new System.Collections.Concurrent.BlockingCollection<(Bitmap Frame, int Index)>(1);
        int examined = 0, decodedCount = 0;
        bool stoppedEarly = false;
        object gate = new();

        var producer = Task.Run(() =>
        {
            var signature = new byte[SignatureLength];
            var previousSignature = new byte[SignatureLength];
            bool hasPrevious = false;
            int previousWidth = 0, previousHeight = 0;
            try
            {
                foreach (var frame in StableFrames())
                {
                    if (cts.IsCancellationRequested)
                        break;
                    int index = ++examined; // producer-only until the final barrier
                    FrameSignature(frame, signature);
                    bool duplicate = hasPrevious && frame.Width == previousWidth && frame.Height == previousHeight &&
                                     MeanAbsDiff(signature, previousSignature) < DuplicateThreshold;
                    (previousSignature, signature) = (signature, previousSignature);
                    previousWidth = frame.Width;
                    previousHeight = frame.Height;
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
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A cancellation-aware source may surface cancellation from MoveNext after the
                // completing frame. That is the successful early-stop path, not a producer fault.
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        var workerTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            try
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
                            SuccessfulShardAdmission retention = successful.TryAdmitOwned(shard);
                            if (retention.Kind == SuccessfulShardAdmissionKind.InconsistentFamily)
                                throw successful.FamilyMismatchException();
                            if (retention.Kind == SuccessfulShardAdmissionKind.Refused)
                                throw successful.LimitException();
                            if (retention.Kind is SuccessfulShardAdmissionKind.Duplicate or
                                SuccessfulShardAdmissionKind.TerminalConflict)
                                continue;
                            CandidateAdmission admission = AdmitCandidate(shards, seen, shard);
                            if (admission != CandidateAdmission.Added)
                            {
                                if (admission == CandidateAdmission.Conflict)
                                {
                                    successful.ReleaseAppliedConflict(shard.Header);
                                    log($"  conflict frame {index}  (ordinal {(long)shard.Header.Index + 1} is now an erasure)");
                                }
                                continue;
                            }
                            successful.MarkReturnedExternal([shard]);
                            string which = shard.Header.IsParity
                                ? $"parity #{(long)shard.Header.Index + 1}"
                                : $"part {(long)shard.Header.Index + 1}/{shard.Header.Count}";
                            log($"  ok      frame {index}  ({which}, {shard.Payload.Length:N0} bytes) — {shards.Count} collected");
                            if (parityReassembler.IsSetComplete(shards))
                            {
                                stoppedEarly = true;
                                cts.Cancel();
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException
                                               and not ShardResourceLimitException and not ShardFamilyMismatchException)
                    {
                        // torn, malformed or non-shard frame — the stream brings it around again
                    }
                }
            }
            catch
            {
                // A run-fatal worker failure must stop a live/cancellation-aware producer now.
                // Otherwise another worker can consume forever and Task.WaitAll never reaches
                // the producer join, or the method can unwind while that producer still owns the
                // enumerator and queue.
                cts.Cancel();
                throw;
            }
        })).ToArray();

        ExceptionDispatchInfo? workerFailure = null;
        try
        {
            Task.WhenAll(workerTasks).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // GetAwaiter unwraps Task.WhenAll's AggregateException. Preserve that first fatal
            // worker error, but join the producer before rethrowing so no background access can
            // race disposal of the queue, cancellation source, or frame enumerator.
            workerFailure = ExceptionDispatchInfo.Capture(ex);
            cts.Cancel();
        }

        ExceptionDispatchInfo? producerFailure = null;
        try
        {
            producer.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            producerFailure = ExceptionDispatchInfo.Capture(ex);
        }

        if (workerFailure is not null)
            workerFailure.Throw();
        if (producerFailure is not null)
            producerFailure.Throw();
        stats = new VideoDecodeStats(examined, decodedCount, shards.Count, stoppedEarly);
        return stoppedEarly || parityReassembler.IsSetComplete(shards);
    }

    private enum CandidateAdmission
    {
        Added,
        Duplicate,
        Conflict,
    }

    /// <summary>
    /// A CRC-valid duplicate with different bytes is not safely first-wins: either candidate may
    /// be the poisoned one. Remove the ordinal entirely so cross-shard recovery can reconstruct it;
    /// once conflicted, later copies cannot silently become authoritative again.
    /// </summary>
    private static CandidateAdmission AdmitCandidate(List<DecodedShard> shards,
        Dictionary<(ulong FileId, int Index, bool Parity), DecodedShard?> seen, DecodedShard candidate)
    {
        var key = (candidate.Header.FileId, candidate.Header.Index, candidate.Header.IsParity);
        if (!seen.TryGetValue(key, out DecodedShard? existing))
        {
            seen.Add(key, candidate);
            shards.Add(candidate);
            return CandidateAdmission.Added;
        }
        if (existing is null)
            return CandidateAdmission.Duplicate;
        if (existing.Header.HasSameFamilyAs(candidate.Header) &&
            existing.Header.PayloadLength == candidate.Header.PayloadLength &&
            existing.Header.PayloadCrc32 == candidate.Header.PayloadCrc32 &&
            existing.Payload.AsSpan().SequenceEqual(candidate.Payload))
            return CandidateAdmission.Duplicate;

        shards.Remove(existing);
        seen[key] = null;
        return CandidateAdmission.Conflict;
    }

    internal static int BudgetedLiveWorkers(Bitmap frame, int requestedWorkers, int budgetMB)
    {
        if (requestedWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(requestedWorkers));
        ShardDecoder.ValidateImageDimensions(frame.Width, frame.Height, budgetMB);
        long perWorker = checked((long)frame.Width * frame.Height * ShardDecoder.ScratchBytesPerPixel);
        return (int)Math.Clamp(checked(budgetMB * 1_000_000L) / perWorker, 1, requestedWorkers);
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
