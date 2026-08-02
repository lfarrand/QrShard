using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
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
    /// <summary>
    /// A path supplied to the shard decoder represents one captured image, even when its container
    /// happens to support animation. Without MaxFrames, ImageSharp materializes every frame before
    /// this class copies only the root frame; a tiny many-frame WebP/TIFF can therefore bypass the
    /// per-worker memory estimate by orders of magnitude. RecordingFrameSource intentionally uses
    /// different options because its contract is to enumerate every frame of a recording.
    /// </summary>
    internal static DecoderOptions NewShardImageDecoderOptions() => new()
    {
        MaxFrames = 1,
        SkipMetadata = true,
    };

    /// <summary>Default wiring for tests, benchmarks, and non-DI callers.</summary>
    public ShardDecoder() : this(
        AppSettings.BuiltIn, new CameraRectifier(), new FrameLocator(new InnerRectScanner(), new StripReader()),
        new StripReader(), new GridSampler(), new ShardAssembler(),
        new Fec(), new Crc(), new FastPngReader(), new PhotoFusion(), new Interleaver2())
    {
    }

    /// <summary>Worker count used when DecodeMaxParallelism is 0 (automatic); see CollectShards
    /// for what the ceiling is measured against.</summary>
    public static int AutoParallelism => Math.Min(Environment.ProcessorCount, 24);

    /// <summary>Comparer that never collapses two names which the host can represent distinctly.</summary>
    internal static StringComparer FileSystemPathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Enumerates and sorts an input set without first allowing a directory iterator or LINQ sort
    /// to materialize attacker-controlled path metadata. The charge includes the actual UTF-16
    /// path length rather than assuming a fixed-size name.
    /// </summary>
    internal static List<string> MaterializeInputPaths(IEnumerable<string> imagePaths, int decodeMemoryBudgetMB)
    {
        int maximumInputs = SuccessfulShardRetentionBudget.MaximumInputCount(decodeMemoryBudgetMB);
        long inputMetadataLimit = SuccessfulShardRetentionBudget.InputMetadataByteLimit(decodeMemoryBudgetMB);
        long inputMetadataBytes = 0;
        var ordered = new List<string>(Math.Min(maximumInputs, 16_384));
        foreach (string path in imagePaths)
        {
            long pathCharge = checked(SuccessfulShardRetentionBudget.InputMetadataBytes + 2L * path.Length);
            if (ordered.Count >= maximumInputs || pathCharge > inputMetadataLimit - inputMetadataBytes)
                throw new ShardResourceLimitException(
                    $"Decode input exceeds the bounded image/path metadata allowance ({maximumInputs:N0} images maximum) for " +
                    $"DecodeMemoryBudgetMB={decodeMemoryBudgetMB:N0}. Split the capture set or raise that setting deliberately.");
            ordered.Add(path);
            inputMetadataBytes += pathCharge;
        }
        ordered.Sort(FileSystemPathComparer);
        return ordered;
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
    public List<DecodedShard> CollectShards(IEnumerable<string> imagePaths, Action<string> log) =>
        CollectShardsCore(imagePaths, log,
            new SuccessfulShardRetentionBudget(settings.DecodeMemoryBudgetMB), emitConflictMarkers: false);

    /// <summary>
    /// Internal long-lived variant used by watch/video-style orchestration. The caller owns the
    /// successful-payload budget, so repeated batches share one ceiling. Newly terminal conflicts
    /// are returned as typed compact markers for the caller to persist/apply, never to assemble.
    /// </summary>
    public List<DecodedShard> CollectShards(IEnumerable<string> imagePaths, Action<string> log,
        SuccessfulShardRetentionBudget successful) =>
        CollectShardsCore(imagePaths, log, successful, emitConflictMarkers: true);

    private List<DecodedShard> CollectShardsCore(IEnumerable<string> imagePaths, Action<string> log,
        SuccessfulShardRetentionBudget successful, bool emitConflictMarkers)
    {
        var ordered = MaterializeInputPaths(imagePaths, settings.DecodeMemoryBudgetMB);
        long conflictSequence = successful.ConflictSequence;
        var resultSlots = Enumerable.Range(0, ordered.Count).Select(_ => new SuccessfulShardSlot()).ToArray();
        var errors = new string?[ordered.Count];

        // One reusable scratch (pixel + visited buffers, the two large per-image allocations)
        // per worker, so decoding N images costs far fewer than 2N GC'd arrays.
        //
        // The cap is a memory ceiling, not a bandwidth one. The old cap of 16 was justified by
        // "PNG decode goes memory-bandwidth-bound past ~16 workers"; that is measurably false —
        // PNG read is 6.5% of a 4K image's decode (~25% at the default density), far too small
        // to explain a ceiling. Throughput keeps climbing well past 16.
        //
        // Measured on an idle 16-core/32-thread part, Max4K, 96 images, 15 round-robin samples
        // (median fps, and peak working set from a separate one-count-per-process run):
        //
        //     workers   16      24      32
        //     med fps   84.5   101.3   109.8
        //     peak WS   5.2 GB  6.7 GB  7.1 GB
        //
        // So 16 -> 24 buys +19.9% for +1.5 GB, and 24 -> 32 buys a further +8.4% for +0.45 GB.
        // Note that 24 is NOT a plateau: on this part 32 workers is genuinely faster, and the
        // gain only looks negligible (+2.4%) if you read the best sample instead of the median.
        // 24 is a deliberate compromise rather than a free optimum — it keeps peak working set
        // under ~7 GB and leaves cores for the rest of the system, and a decode that pages is
        // far slower than one that gives up 8%. Machines with memory to spare should raise it;
        // that is what the setting is for.
        //
        // Re-measure with `dotnet run -c Release -- --par-sweep` (and `--par-mem`, one worker
        // count per process). Override via appsettings.json DecodeMaxParallelism.
        int parallelism = settings.DecodeMaxParallelism;
        if (parallelism <= 0)
            parallelism = AutoParallelism;
        var failures = new FailedCapture?[ordered.Count];
        var salvage = new FailedCaptureRetentionBudget(settings.DecodeMemoryBudgetMB);

        // Each worker takes the next image off a shared cursor rather than owning a pre-assigned
        // range. Per-image decode cost varies several-fold (the camera-rectification fallback, ECC
        // depth, damage), so any up-front split strands the workers that drew the cheap images.
        // The cursor is touched once per image against ~70 ms of 4K decode, so its contention does
        // not show up.
        //
        // How much this is worth depends on whether the image count divides evenly by the worker
        // count. Median fps, idle 16-core/32-thread part, Max4K, 24 workers, 9 samples, ONE
        // configuration per process (`--par-compare`); * marks counts divisible by 24:
        //
        //     images            50      100     120*     200     240*
        //     Parallel.For    83.2     95.4    119.9    96.2    121.1
        //     cursor         100.1    116.8    118.2   122.7    123.7
        //                   +20.3%   +22.4%    -1.4%  +27.5%    +2.1%
        //
        // The mechanism is visible directly in how much of the machine each keeps busy — CPU
        // time over wall time, as a fraction of the 24 workers asked for:
        //
        //     Parallel.For     73%      74%      93%     73%      93%
        //     cursor           83%      90%      93%     94%      96%
        //
        // Chunking strands roughly a quarter of its workers whenever the count does not divide
        // evenly, and that stranding is the whole effect: where it divides evenly (120, 240) the
        // two are level, and everywhere else the cursor wins by keeping the workers fed. Exact
        // divisibility is coincidental — about 1 in 24 counts here — so the ragged case is the
        // normal one.
        //
        // Allocation per pass falls consistently, by 4% (50 images) to 16% (200), because "one
        // scratch per worker" becomes exact: Parallel.For builds thread-locals well past
        // MaxDegreeOfParallelism, recycling ranges across more threads than it runs at once.
        // Peak working set is a wash — it ranged from -22% to +8% across these counts with no
        // trend, which is GC timing rather than a property of either scheme.
        int workers = BudgetedWorkers(ordered, parallelism, settings.DecodeMemoryBudgetMB, log,
            out long plannedMaxPixels);
        int next = -1;
        int fatalStop = 0;

        void Worker()
        {
            var scratch = new DecodeScratch();
            int i;
            while (Volatile.Read(ref fatalStop) == 0 &&
                   (i = Interlocked.Increment(ref next)) < ordered.Count)
            {
                // A peer can publish a run-fatal condition between the loop test and our cursor
                // claim. Treat that claimed item as not started: at most the workers which were
                // already inside DecodeImage when the failure happened may finish expensive work.
                if (Volatile.Read(ref fatalStop) != 0)
                    break;
                var diagnostics = new DecodeDiagnostics
                {
                    TryReserveSalvage = salvage.TryReserve,
                    SuccessfulShardBudget = successful,
                    SuccessfulShardSlot = resultSlots[i],
                };
                try
                {
                    _ = DecodeImage(ordered[i], scratch, diagnostics, plannedMaxPixels);
                    // The axis-aligned attempt can fail and reserve salvage before a camera retry
                    // succeeds. Successful captures do not enter fusion, so return that reservation.
                    salvage.Release(diagnostics);
                }
                catch (ShardSuppressedException)
                {
                    salvage.Release(diagnostics);
                }
                catch (ShardDecodeException ex)
                {
                    bool runFatal = diagnostics.SuccessfulShardAdmissionRefused ||
                                    ex is ShardFamilyMismatchException;
                    if (runFatal)
                        salvage.Release(diagnostics); // a prior camera attempt must not bypass the cap via fusion
                    errors[i] = ex.Message;
                    if (runFatal)
                        Interlocked.Exchange(ref fatalStop, 1);
                    if (!runFatal &&
                        diagnostics is { CellsLayout: not null, Cells: not null })
                        failures[i] = new FailedCapture(diagnostics.CellsLayout, diagnostics.Cells, ordered[i]);
                }
                // Everything above runs on attacker-controlled bytes, and only the failures the
                // pipeline anticipated arrive as ShardDecodeException. Anything else — an index
                // that escaped a bounds check, a divide by zero in the field math — used to escape
                // the worker entirely and abort the batch, throwing away every OTHER image that
                // had already decoded successfully. A folder of 200 good captures could be
                // destroyed by one crafted file. The blast radius of an unanticipated bug in the
                // decode path belongs to the image that caused it.
                //
                // Not a licence to stop validating: the specific defects that motivated this are
                // fixed at their source (odd parity and zero-codeword layouts rejected in
                // Layout.UnpackMetadata, stripe sums in ShardHeader). This is the backstop for the
                // ones not found yet. OOM and cancellation stay fatal — both are conditions of the
                // whole run, not of one image, and continuing past them would be dishonest.
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    salvage.Release(diagnostics);
                    errors[i] = $"unhandled {ex.GetType().Name} while decoding: {ex.Message}";
                }
            }
        }

        if (workers > 0)
        {
            var helpers = new Task[workers - 1];
            for (int w = 0; w < helpers.Length; w++)
                helpers[w] = Task.Run(Worker);
            try
            {
                // The caller decodes too. That saves a thread, and — like the Parallel.For this
                // replaces, which inlined iterations on the calling thread — it keeps the decode
                // making progress when the pool has no thread to spare, which it may not when the
                // caller is itself pool work running alongside other decodes.
                Worker();
            }
            finally
            {
                // Nothing may still be writing into results/failures once this returns, including
                // when the caller's own share threw.
                Task.WaitAll(helpers);
            }
        }

        if (successful.HasInconsistentFamily)
            throw successful.FamilyMismatchException();
        successful.ReleaseBatchConflicts(conflictSequence);

        var shards = new List<DecodedShard>();
        for (int i = 0; i < ordered.Count; i++)
        {
            DecodedShard? shard = resultSlots[i].Shard;
            if (shard is not null)
            {
                shards.Add(shard);
                string corrections = shard.CorrectedBytes > 0 ? $", ECC corrected {shard.CorrectedBytes} bytes" : "";
                string which = shard.Header.IsParity
                    ? $"parity #{(long)shard.Header.Index + 1}"
                    : $"part {(long)shard.Header.Index + 1}/{shard.Header.Count}";
                log($"  ok      {ShardHeader.Display(Path.GetFileName(ordered[i]))}  " +
                    $"({which}, {shard.Payload.Length:N0} bytes{corrections})");
            }
            else if (errors[i] is { } error)
            {
                log($"  FAILED  {ShardHeader.Display(Path.GetFileName(ordered[i]))}: " +
                    ShardHeader.Display(error ?? "unknown decode failure"));
            }
        }

        // The local list now owns these payloads. PhotoFusion runs afterwards and can discover a
        // conflict for one of them; until RemoveAll/caller marker application drops that list
        // reference, its bytes are not honestly reusable.
        successful.MarkReturnedExternal(shards);

        if (successful.RefusedCount > 0)
            throw successful.LimitException();

        // Multi-capture fusion: several failed captures of the same shard may still combine
        // into a valid one (glare and reflections move between shots).
        var failed = failures.OfType<FailedCapture>().ToList();
        if (failed.Count >= 2)
        {
            foreach (var shard in photoFusion.Fuse(failed, log))
            {
                // Preserve a second CRC-valid candidate even when its ordinal already exists.
                // Reassembly compares bytes: identical captures collapse harmlessly, while a
                // disagreement becomes an erasure rather than a filename-order first-wins choice.
                var slot = new SuccessfulShardSlot();
                SuccessfulShardAdmission admission = successful.TryAdmitOwned(shard, slot);
                if (admission.Kind == SuccessfulShardAdmissionKind.InconsistentFamily)
                    throw successful.FamilyMismatchException();
                if (admission.Kind == SuccessfulShardAdmissionKind.Refused)
                    throw successful.LimitException();
                if (admission.Kind == SuccessfulShardAdmissionKind.Added &&
                    successful.TryPublish(shard.Header, slot, shard))
                {
                    shards.Add(shard);
                    // A later fused candidate can conflict with this one in the same iterator.
                    // Transfer ownership immediately rather than after the whole fusion pass.
                    successful.MarkReturnedExternal([shard]);
                }
            }
        }
        shards.RemoveAll(s => successful.IsConflicted(s.Header));
        if (emitConflictMarkers)
            shards.AddRange(successful.ConflictMarkersSince(conflictSequence));
        if (salvage.RefusedCount > 0)
            log($"  skipped fusion salvage for {salvage.RefusedCount:N0} failed capture(s): " +
                "the run-wide DecodeMemoryBudgetMB/fusion-group limit was reached.");
        return shards;
    }

    public DecodedShard DecodeImage(string path) => DecodeImage(path, new DecodeScratch());

    /// <summary>Diagnostic single-image decode: captures the layout and per-codeword ECC
    /// statistics whether or not the decode succeeds, with the same camera-rectification
    /// fallback as the normal pipeline — so photo captures diagnose (and calibrate) too.</summary>
    public DecodeDiagnostics Diagnose(string path)
    {
        var scratch = new DecodeScratch();
        var diagnostics = new DecodeDiagnostics { WantDetail = true };
        try
        {
            Bitmap bmp = LoadBitmap(path, scratch);
            try
            {
                diagnostics.Shard = DecodeBitmap(bmp, scratch, path, diagnostics);
            }
            catch (ShardDecodeException axisAlignedError)
            {
                Bitmap? rectified;
                string? cameraRefusal = null;
                try
                {
                    rectified = cameraRectifier.TryRectify(bmp);
                }
                catch (ShardDecodeException ex)
                {
                    // Keep WHY the camera path declined. Swallowing it discarded the only message
                    // that named something the user could act on — see the note at the sibling
                    // site in DecodeImage.
                    rectified = null;
                    cameraRefusal = ex.Message;
                }
                if (rectified is null)
                    throw cameraRefusal is null
                        ? axisAlignedError
                        : new ShardDecodeException($"{axisAlignedError.Message} (camera capture: {cameraRefusal})");
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
            var info = Image.Identify(NewShardImageDecoderOptions(), imageBytes);
            ValidateImageDimensions(info.Width, info.Height, settings.DecodeMemoryBudgetMB);
            image = Image.Load<Rgb24>(NewShardImageDecoderOptions(), imageBytes);
        }
        // Everything except the conditions that belong to the whole run, and ShardDecodeException
        // which is already the typed failure. Enumerating types was wrong here for the same reason
        // it was wrong in the worker-sizing probe: ImageSharp inflates ancillary PNG text chunks,
        // so a malformed zTXt raises System.IO.InvalidDataException, which derives from
        // SystemException and matched neither listed type. These bytes are attacker-controlled and
        // this path has no outer net — it fed the exception straight out of the library.
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException
                                       and not ShardDecodeException)
        {
            throw new ShardDecodeException($"Not a readable image ({ShardHeader.Display(ex.Message)}).");
        }
        Bitmap bmp;
        using (image)
        {
            bmp = ToBitmap(image, scratch);
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
            string? cameraRefusal = null;
            try
            {
                rectified = cameraRectifier.TryRectify(bmp);
            }
            catch (ShardDecodeException ex)
            {
                // The camera path can decline for a reason the user can act on, and the message
                // saying so was being thrown away. AdaptiveBinarizer refuses a photo over 80
                // megapixels with "Image is WxH; too large for camera-capture binarization ...
                // Crop closer to the shard, or capture at a lower resolution." — composed
                // deliberately, and then unreachable, because all three TryRectify call sites
                // swallowed it and rethrew the axis-aligned error instead. A real 9000x9000 photo
                // reported "Could not locate the black frame", which is both wrong and unhelpful.
                //
                // The same file is explicit that the OTHER "too large" refusal, ToBitmap's, is
                // deliberately allowed to propagate. One oversize message was preserved on purpose
                // and its neighbour discarded unconditionally.
                rectified = null;
                cameraRefusal = ex.Message;
            }
            if (rectified is null)
                throw cameraRefusal is null
                    ? axisAlignedError
                    : new ShardDecodeException($"{axisAlignedError.Message} (camera capture: {cameraRefusal})");
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

    private DecodedShard DecodeImage(string path, DecodeScratch scratch, DecodeDiagnostics? diagnostics,
        long plannedMaxPixels = 0)
    {
        Bitmap bmp = LoadBitmap(path, scratch, plannedMaxPixels);

        try
        {
            return DecodeBitmap(bmp, scratch, path, diagnostics);
        }
        catch (ShardSuppressedException)
        {
            throw; // duplicate/conflict outcome, never evidence that rectification is needed
        }
        catch (ShardFamilyMismatchException)
        {
            throw; // set-level integrity failure, not evidence that rectification is needed
        }
        catch (ShardDecodeException) when (diagnostics?.SuccessfulShardAdmissionRefused == true)
        {
            // This is a run-wide resource refusal, not evidence of perspective distortion. A
            // camera retry would repeat expensive rectification and count the same valid shard a
            // second time before arriving at the identical budget decision.
            throw;
        }
        catch (ShardDecodeException axisAlignedError)
        {
            // Camera fallback: photos are rotated/perspective-distorted, which the axis-aligned
            // pipeline cannot handle. If the image carries camera-profile finder patterns,
            // rectify it into an axis-aligned canvas and run the same pipeline on that.
            Bitmap? rectified;
            string? cameraRefusal = null;
            try
            {
                rectified = cameraRectifier.TryRectify(bmp);
            }
            catch (ShardDecodeException ex)
            {
                // The camera path can decline for a reason the user can act on, and the message
                // saying so was being thrown away. AdaptiveBinarizer refuses a photo over 80
                // megapixels with "Image is WxH; too large for camera-capture binarization ...
                // Crop closer to the shard, or capture at a lower resolution." — composed
                // deliberately, and then unreachable, because all three TryRectify call sites
                // swallowed it and rethrew the axis-aligned error instead. A real 9000x9000 photo
                // reported "Could not locate the black frame", which is both wrong and unhelpful.
                //
                // The same file is explicit that the OTHER "too large" refusal, ToBitmap's, is
                // deliberately allowed to propagate. One oversize message was preserved on purpose
                // and its neighbour discarded unconditionally.
                rectified = null;
                cameraRefusal = ex.Message;
            }
            if (rectified is null)
                throw cameraRefusal is null
                    ? axisAlignedError
                    : new ShardDecodeException($"{axisAlignedError.Message} (camera capture: {cameraRefusal})");

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
    private Bitmap LoadBitmap(string path, DecodeScratch scratch, long plannedMaxPixels = 0)
    {
        try
        {
            // Hold one file object from identification through decode. Reopening the pathname for
            // the fast PNG reader or ImageSharp fallback created a check/use race: a producer in a
            // watched/shared directory could replace a small identified file with a huge one and
            // bypass this admission check. On Unix a renamed/replaced path does not alter the open
            // handle; on Windows FileShare.Read also refuses rename/delete while it is open.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                FileOptions.SequentialScan);
            var options = NewShardImageDecoderOptions();
            var info = Image.Identify(options, stream);
            long pixels = checked((long)info.Width * info.Height);
            if (plannedMaxPixels > 0 && pixels > plannedMaxPixels)
                throw new ShardDecodeException(
                    $"Image dimensions changed after parallel memory planning ({info.Width:N0}x{info.Height:N0}, " +
                    $"larger than the planned {plannedMaxPixels:N0} pixels). Retry after the input files stop changing.");
            ValidateImageDimensions(info.Width, info.Height, settings.DecodeMemoryBudgetMB);
            stream.Position = 0;
            if (pngReader.TryRead(stream, scratch, out Bitmap bmp))
                return bmp;
            stream.Position = 0;
            using var image = Image.Load<Rgb24>(options, stream);
            return ToBitmap(image, scratch);
        }
        // A missing/deleted file, a directory path, a permission error, or a recognized-but-
        // unsupported image (ImageSharp throws NotSupportedException for those) must all surface
        // as the typed decode failure — so the session API returns an error result and
        // DecodeFolder's per-image catch handles it — never leak a raw exception. TryRead is
        // inside the try because its FileStream open throws UnauthorizedAccessException on a
        // directory/ACL path, which its own catch filter does not swallow. ToBitmap's
        // ShardDecodeException ("too large") is deliberately not in this filter, so it propagates.
        // Broad by policy, not by enumeration. The list here missed InvalidDataException — raised
        // when ImageSharp inflates a malformed zTXt chunk — and the comment above states exactly
        // the contract that break violated. ShardDecodeException stays excluded so ToBitmap's
        // "too large" propagates with its own message rather than being re-wrapped; OOM and
        // cancellation stay fatal because they describe the run, not this image.
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException
                                       and not ShardDecodeException)
        {
            throw new ShardDecodeException($"Not a readable image ({ShardHeader.Display(ex.Message)}).");
        }
    }

    /// <summary>
    /// Planning estimate for the peak bytes a worker holds per pixel of its source image. The
    /// grid-sized buffers are smaller by the cell size and are not the term that matters.
    /// </summary>
    // Counted, not guessed. 4 was an undercount by roughly 6x, which matters because the whole
    // point of this budget is to stop 24 workers deciding together that they can afford an image
    // they cannot. Concurrently live, per source pixel:
    //
    //     3   ImageSharp's own Image<Rgb24>, live while ToBitmap copies out of it
    //     3   DecodeScratch.Pixels (Rgb24)
    //     1   DecodeScratch.ClearedVisited (bool)
    //     1   AdaptiveBinarizer lum (byte)
    //    16   AdaptiveBinarizer integral + integralSq (two long[], 8 bytes each)
    //     1   AdaptiveBinarizer dark (bool)
    //    ---
    //    25   common-path managed arrays
    //     3   camera rectification canvas when the axis-aligned path does not decode
    //
    // The two Sauvola integral images dominate and were simply never in the old 4-byte estimate.
    // Allocator pools, row structures, temporary candidates, GC overlap, and the camera path mean
    // the sum above is not a process-memory ceiling. Six 48 MP rotated camera-profile inputs were
    // measured at roughly 38 bytes/source-pixel of private memory, so use 40 for worker planning.
    // This is deliberately described as an estimate: the setting throttles concurrency; it cannot
    // promise an exact working-set limit across codecs, allocators, and runtimes.
    internal const int ScratchBytesPerPixel = 40;

    // The concurrency estimate above includes the camera fallback's large integral-image arrays.
    // It must not also be used as the single-image admission price: clean shards take the fast,
    // axis-aligned path and peak at roughly two RGB24 surfaces (the decoder plus DecodeScratch),
    // and QrShard can legitimately render the full 16,384 x 16,384 supported canvas. Charging
    // those files for a fallback they never use made the default 4 GB budget reject QrShard's own
    // output above about 100 MP. The fallback itself refuses inputs above 80 MP before allocating
    // its integral images, so six bytes/pixel is the relevant pre-load safety gate.
    private const int SingleImageBytesPerPixel = 6;

    /// <summary>
    /// Enforces a conservative pre-load budget for one image. Worker concurrency is planned with
    /// the larger full-pipeline estimate separately; this gate covers the two RGB24 surfaces that
    /// may coexist while an image is loaded without rejecting the tool's own largest canvas.
    /// </summary>
    internal static void ValidateImageDimensions(int width, int height, int budgetMB)
    {
        if (width < 1 || height < 1)
            throw new ShardDecodeException($"Image declares invalid dimensions {width}x{height}.");
        long pixels = checked((long)width * height);
        long planned = pixels > long.MaxValue / SingleImageBytesPerPixel
            ? long.MaxValue
            : pixels * SingleImageBytesPerPixel;
        long budget = checked(budgetMB * 1_000_000L);
        if (pixels > MaxDecodablePixels || planned > budget)
            throw new ShardDecodeException(
                $"Image is {width:N0}x{height:N0} (~{planned / 1_000_000:N0} MB planned decode memory), " +
                $"above the {budgetMB:N0} MB DecodeMemoryBudgetMB. Resize/crop it or raise that setting deliberately.");
    }

    /// <summary>
    /// Worker count for a decode, capped by a memory budget as well as by parallelism.
    ///
    /// ShardEncoder has always derived its degree from EncodeMemoryBudgetMB, because it knows the
    /// canvas size it chose. The decode side had no counterpart: it took min(parallelism, images)
    /// and each worker then sized its scratch from the largest image IT happened to see. With a
    /// per-image ceiling of 500M pixels that is ~20 GB of planned memory per worker, times up to 24 —
    /// and the dimensions are the sender's choice, not the receiver's.
    ///
    /// The sizes are knowable before any decoding: Image.Identify reads a header without touching
    /// pixel data, for every format the decoder accepts. It receives the same one-frame,
    /// metadata-free options as the subsequent load, so neither the estimate nor the load expands
    /// an animation that arrived through the ordinary image path. The cost is one header read per
    /// file against a full decode of each afterwards.
    ///
    /// Unreadable files are skipped rather than guessed at — they will fail to decode on their own
    /// merits, and letting a corrupt header influence the pool size would hand the attacker the
    /// very knob this is closing. If none can be identified the safe fallback is one worker.
    /// The largest planned pixel count is also enforced on the same open handle used for the
    /// actual load, so a watched/shared-directory path cannot be replaced or renamed to a larger
    /// image after planning. On Unix this does not prevent a separately authorised writer from
    /// modifying the same inode in place during the narrow identify/load window.
    /// </summary>
    private static int BudgetedWorkers(List<string> images, int parallelism, int budgetMB, Action<string> log,
        out long largestPixels)
    {
        int ceiling = Math.Min(parallelism, images.Count);
        largestPixels = 0;
        if (ceiling <= 1)
            return Math.Max(ceiling, 0);

        foreach (string path in images)
        {
            try
            {
                var info = Image.Identify(NewShardImageDecoderOptions(), path);
                largestPixels = Math.Max(largestPixels, (long)info.Width * info.Height);
            }
            // Deliberately everything except the two conditions that belong to the whole run. An
            // enumerated filter was wrong here and wrong in a way worth remembering: Identify also
            // inflates ancillary text chunks, so a PNG carrying a malformed zTXt raises
            // System.IO.InvalidDataException — which derives from SystemException, not
            // IOException, and so escaped a filter that listed IOException. This runs BEFORE any
            // worker, outside the per-image catch below, so one 88-byte crafted file killed the
            // entire decode: exactly the failure that catch was added to prevent, reintroduced a
            // screen away from it.
            //
            // The policy needs no enumeration anyway. A file this cannot read contributes nothing
            // to a size estimate by definition, and the decode pass diagnoses it properly a moment
            // later. Nothing here should ever be fatal to the batch.
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // Not identifiable: leave it out of the estimate.
            }
        }

        if (largestPixels <= 0)
        {
            // There is no trustworthy size with which to divide the budget. One worker is the
            // fail-safe choice; each actual file will still be identified and admission-checked
            // on its stable handle. This also closes a replace-after-probe attack where every path
            // is initially unreadable and then becomes a large valid image.
            log("  using 1 decode worker: no input image could be identified for memory planning.");
            return 1;
        }

        long perWorker = largestPixels > long.MaxValue / ScratchBytesPerPixel
            ? long.MaxValue
            : largestPixels * ScratchBytesPerPixel;
        int affordable = (int)Math.Clamp(budgetMB * 1_000_000L / perWorker, 1, ceiling);
        if (affordable < ceiling)
            log($"  using {affordable} decode worker(s) instead of {ceiling}: the largest image is " +
                $"{largestPixels:N0} pixels (~{perWorker / 1_000_000:N0} MB planned each) against a " +
                $"{budgetMB:N0} MB budget (appsettings.json DecodeMemoryBudgetMB).");
        return affordable;
    }

    private const long MaxDecodablePixels = 500_000_000; // matches FastPngReader's cap

    /// <summary>Copies a decoded ImageSharp frame into a pooled Bitmap, rejecting an implausibly
    /// large image before the pixel-count multiply can overflow int.</summary>
    private static Bitmap ToBitmap(Image<Rgb24> image, DecodeScratch scratch)
    {
        if ((long)image.Width * image.Height > MaxDecodablePixels)
            throw new ShardDecodeException("Image is too large to decode.");
        int count = image.Width * image.Height;
        var px = scratch.Pixels(count);
        image.CopyPixelDataTo(px.AsSpan(0, count));
        return new Bitmap(px, image.Width, image.Height);
    }

    public DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path) =>
        DecodeBitmap(bmp, scratch, path, null);

    private DecodedShard DecodeBitmap(Bitmap bmp, DecodeScratch scratch, string path, DecodeDiagnostics? diagnostics)
    {
        ValidateImageDimensions(bmp.Width, bmp.Height, settings.DecodeMemoryBudgetMB);
        var (layout, inner) = frameLocator.Locate(bmp, scratch);
        // BEFORE anything is sized from it. This check used to live only in ReadDataGrid, three
        // statements further on, so the diagnostics allocation below and every heatmap downstream
        // of it were sized from a strip nothing had yet called impossible.
        layout.RequireResolvableIn(inner.W, inner.H);
        // Capture per-cell classification margins for diagnostics only — the frame located, so a
        // quality heatmap can show WHERE a capture is weak even if the grid decode later fails.
        int[]? cellMargins = diagnostics is { WantDetail: true } ? new int[(long)layout.GridW * layout.GridH] : null;
        if (diagnostics is not null)
        {
            diagnostics.Layout = layout;
            diagnostics.CellMargins = cellMargins;
        }
        var palette = stripReader.ReadPalette(bmp, inner, layout);
        byte[] cells = gridSampler.ReadDataGrid(bmp, inner, layout, palette, scratch,
            out bool[]? suspectBytes, out byte[]? secondChoiceBytes, cellMargins);

        // v2 interleave: gather the permuted cell stream back into classic order so the whole
        // SIMD/erasure/Chase machinery — and multi-capture fusion — run unchanged.
        byte[] work = cells;
        bool[]? workSuspects = suspectBytes;
        byte[]? workSecond = secondChoiceBytes;
        int protectedLength = layout.CodewordCount * Fec.CodewordLength;
        if (layout.Interleave2 && layout.EccParity > 0)
        {
            var gathered = scratch.GatheredCells(protectedLength);
            work = gathered;
            bool[]? flags = null;
            if (suspectBytes is not null)
            {
                flags = scratch.GatheredFlags(protectedLength);
                workSuspects = flags;
            }
            byte[]? second = null;
            if (secondChoiceBytes is not null)
            {
                second = scratch.GatheredSecond(protectedLength);
                workSecond = second;
            }
            interleaver.GatherStreams(cells, gathered, suspectBytes, flags,
                secondChoiceBytes, second, protectedLength);
        }

        // Copy the (classic-order) cells into the diagnostics on failure — the raw material
        // for multi-capture fusion. First failing attempt wins (scratch buffers are reused).
        void Salvage()
        {
            // Only the folder path supplies an admission hook and only ECC layouts are fusible.
            // Diagnose consumes margins/codeword errors, not a private copy of the cell stream.
            if (diagnostics is not null && diagnostics.Cells is null && layout.EccParity > 0 &&
                diagnostics.TryReserveSalvage is not null)
            {
                int salvageLength = protectedLength;
                if (!diagnostics.TryReserveSalvage(layout, salvageLength))
                    return;
                diagnostics.Cells = work.AsSpan(0, salvageLength).ToArray();
                diagnostics.SalvageReservedBytes = salvageLength;
                // Recorded WITH the cells, not read back later: Layout is overwritten by the
                // camera-rectified retry while these cells stay from the first attempt.
                diagnostics.CellsLayout = layout;
            }
        }

        byte[] stream;
        int correctedBytes = 0;
        if (layout.EccParity > 0)
        {
            stream = scratch.Recovered(layout.CodewordCount * Fec.DataLength(layout.EccParity));
            int[]? codewordErrors = diagnostics is { WantDetail: true } ? new int[layout.CodewordCount] : null;
            bool recovered = fec.TryRecoverInto(work, layout.EccParity, layout.CodewordCount, stream, out correctedBytes, codewordErrors, workSuspects, workSecond);
            if (diagnostics is not null && codewordErrors is not null)
                diagnostics.CodewordErrors = codewordErrors;
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

        // Preserve the actionable upgrade diagnostic even though the shared deserializer also
        // refuses unknown bits for session/fusion callers. Byte 5 is the flags field after QRS1
        // magic + version; only interpret it when the magic is present.
        byte unknownFlags = stream.Length > 5 && stream.AsSpan(0, 4).SequenceEqual(ShardHeader.Magic)
            ? (byte)(stream[5] & ~ShardHeader.KnownFlags)
            : (byte)0;
        if (unknownFlags != 0)
            throw new ShardDecodeException(
                $"This shard uses features from a newer QrShard (unknown flags 0x{unknownFlags:X2}). Update QrShard to decode it.");

        var header = ShardHeader.Deserialize(stream, out int headerLen);
        if (header is null)
        {
            Salvage();
            throw new ShardDecodeException("Shard header is corrupt. Recapture this image.");
        }
        if ((long)headerLen + header.PayloadLength > stream.Length) // long: never overflow on a crafted length
        {
            Salvage();
            throw new ShardDecodeException("Shard header declares more payload than the image holds.");
        }
        ReadOnlySpan<byte> payloadBytes = stream.AsSpan(headerLen, header.PayloadLength);
        if (crc.Crc32(payloadBytes) != header.PayloadCrc32)
        {
            Salvage();
            throw new ShardDecodeException(
                $"Payload CRC-32 mismatch (part {(long)header.Index + 1}/{header.Count}). Recapture this image.");
        }
        // Header names are retained as UTF-16 strings but arrive as UTF-8 bytes, so twice the
        // wire-header length conservatively covers their worst retained representation.
        int retentionCharge = checked(2 * headerLen + 2 * path.Length + header.PayloadLength +
            SuccessfulShardRetentionBudget.PerShardOverheadBytes);
        byte[] payload;
        SuccessfulShardRetentionBudget? successfulBudget = diagnostics?.SuccessfulShardBudget;
        SuccessfulShardSlot? successfulSlot = diagnostics?.SuccessfulShardSlot;
        if (successfulBudget is not null && successfulSlot is not null)
        {
            SuccessfulShardAdmission admission = successfulBudget.TryAdmit(
                header, payloadBytes, retentionCharge, path, layout.EccParity, correctedBytes, successfulSlot);
            if (admission.Kind == SuccessfulShardAdmissionKind.Refused)
            {
                diagnostics!.SuccessfulShardAdmissionRefused = true;
                throw new ShardResourceLimitException(
                    $"Valid shard {(long)header.Index + 1} was not retained because decoded payloads reached " +
                    $"DecodeMemoryBudgetMB={settings.DecodeMemoryBudgetMB:N0}. Split the capture set or raise that setting deliberately.");
            }
            if (admission.Kind == SuccessfulShardAdmissionKind.InconsistentFamily)
                throw successfulBudget.FamilyMismatchException();
            if (admission.Kind != SuccessfulShardAdmissionKind.Added)
                throw new ShardSuppressedException();
            payload = admission.Payload!;
        }
        else
        {
            payload = payloadBytes.ToArray();
        }
        var shard = new DecodedShard(header, payload, path, layout.EccParity, correctedBytes);
        if (successfulBudget is not null && successfulSlot is not null &&
            !successfulBudget.TryPublish(header, successfulSlot, shard))
            throw new ShardSuppressedException();
        return shard;
    }

    /// <summary>
    /// Run-wide admission for successful folder payloads. Worker scratch is reusable, but every
    /// accepted DecodedShard remains live until reassembly; without this independent ceiling a
    /// mixed folder could retain an arbitrary number of otherwise-valid files.
    /// </summary>
    internal sealed class SuccessfulShardRetentionBudget
    {
        internal const int PerShardOverheadBytes = 256;
        internal const int InputMetadataBytes = 64;
        private const int InputMetadataBudgetDivisor = 8;
        private const int AbsoluteMaximumInputs = 1_000_000;
        private readonly object gate = new();
        private readonly long byteLimit;
        private readonly int countLimit;
        private readonly Dictionary<(ulong FileId, int Index, bool Parity), Candidate> candidates = [];
        private readonly Dictionary<ulong, ShardHeader> families = [];
        private long retainedBytes;
        private int retainedCount;
        private int refusedCount;
        private ulong? inconsistentFamilyFileId;

        private sealed class Candidate(ShardHeader header, byte[] payload, int retainedBytes,
            string sourceFile, int eccParity, int correctedBytes, SuccessfulShardSlot? slot,
            bool payloadReleasableOnConflict)
        {
            internal ShardHeader Header { get; } = header;
            internal byte[]? Payload { get; set; } = payload;
            internal int RetainedBytes { get; set; } = retainedBytes;
            internal string SourceFile { get; } = sourceFile;
            internal int EccParity { get; } = eccParity;
            internal int CorrectedBytes { get; } = correctedBytes;
            internal SuccessfulShardSlot? Slot { get; set; } = slot;
            internal bool PayloadReleasableOnConflict { get; set; } = payloadReleasableOnConflict;
            internal bool Conflicted { get; set; }
            internal long ConflictSequence { get; set; }
        }

        private long conflictSequence;

        internal SuccessfulShardRetentionBudget(int decodeMemoryBudgetMB)
        {
            byteLimit = checked(decodeMemoryBudgetMB * 1_000_000L);
            countLimit = MaximumInputCount(decodeMemoryBudgetMB);
        }

        internal static long InputMetadataByteLimit(int decodeMemoryBudgetMB) =>
            checked(decodeMemoryBudgetMB * 1_000_000L) / InputMetadataBudgetDivisor;

        internal static int MaximumInputCount(int decodeMemoryBudgetMB) =>
            MaximumInputCountForByteLimit(checked(decodeMemoryBudgetMB * 1_000_000L));

        internal static int MaximumInputCountForByteLimit(long byteLimit) =>
            (int)Math.Min(AbsoluteMaximumInputs,
                Math.Max(0, byteLimit) / InputMetadataBudgetDivisor / InputMetadataBytes);

        internal long RetainedBytes
        {
            get { lock (gate) return retainedBytes; }
        }

        internal int RetainedCount
        {
            get { lock (gate) return retainedCount; }
        }

        internal int RefusedCount
        {
            get { lock (gate) return refusedCount; }
        }

        internal long ConflictSequence
        {
            get { lock (gate) return conflictSequence; }
        }

        /// <summary>
        /// Admits at most one full payload for an ordinal. Exact duplicates consume nothing. A
        /// disagreement atomically revokes the first result slot, releases its payload charge and
        /// records one typed compact marker; later copies remain terminal and allocate nothing.
        /// </summary>
        internal SuccessfulShardAdmission TryAdmit(ShardHeader header, ReadOnlySpan<byte> payload,
            int bytes, string sourceFile, int eccParity, int correctedBytes, SuccessfulShardSlot slot) =>
            TryAdmitCore(header, payload, bytes, sourceFile, eccParity, correctedBytes, slot,
                ownedPayload: null, payloadReleasableOnConflict: true);

        /// <summary>Direct admission probe used by focused resource-bound tests.</summary>
        internal SuccessfulShardAdmission TryAdmit(ShardHeader header, ReadOnlySpan<byte> payload,
            int bytes) => TryAdmit(header, payload, bytes, "test", 0, 0, new SuccessfulShardSlot());

        internal SuccessfulShardAdmission TryAdmitOwned(DecodedShard shard,
            SuccessfulShardSlot? slot = null, bool payloadReleasableOnConflict = true) => TryAdmitCore(
            shard.Header, shard.Payload, RetentionCharge(shard), shard.SourceFile, shard.EccParity,
            shard.CorrectedBytes, slot, shard.Payload, payloadReleasableOnConflict);

        internal void Seed(IEnumerable<DecodedShard> shards)
        {
            foreach (DecodedShard shard in shards)
            {
                if (shard.IsTerminalConflict)
                    continue;
                // Session/caller lists still own these arrays throughout the collection call. A
                // conflict cannot honestly free their allowance until that external state has
                // applied the marker, so keep the charge reserved for this run.
                SuccessfulShardAdmissionKind kind = TryAdmitOwned(shard,
                    payloadReleasableOnConflict: false).Kind;
                if (kind == SuccessfulShardAdmissionKind.InconsistentFamily)
                    throw FamilyMismatchException();
                if (kind == SuccessfulShardAdmissionKind.Refused)
                    throw LimitException();
            }
        }

        internal List<DecodedShard> ConflictMarkersSince(long sequence)
        {
            lock (gate)
            {
                return candidates.Values
                    .Where(c => c.Conflicted && c.ConflictSequence > sequence)
                    .OrderBy(c => c.ConflictSequence)
                    .Select(c => new DecodedShard(c.Header, [], c.SourceFile, c.EccParity, c.CorrectedBytes)
                    {
                        IsTerminalConflict = true,
                    })
                    .ToList();
            }
        }

        internal bool IsConflicted(ShardHeader header)
        {
            lock (gate)
                return candidates.TryGetValue((header.FileId, header.Index, header.IsParity),
                    out Candidate? candidate) && candidate.Conflicted;
        }

        /// <summary>
        /// Releases externally seeded payload charges only after the caller has durably applied
        /// the typed markers and replaced its old shard list. Calling this before that hand-off
        /// would make the accounting optimistic while the arrays are still live.
        /// </summary>
        internal void ReleasePersistedConflicts(IEnumerable<DecodedShard> markers)
        {
            lock (gate)
            {
                foreach (DecodedShard marker in markers)
                {
                    if (!marker.IsTerminalConflict ||
                        !candidates.TryGetValue((marker.Header.FileId, marker.Header.Index,
                            marker.Header.IsParity), out Candidate? candidate))
                        continue;
                    ReleaseAppliedConflict(candidate);
                }
            }
        }

        /// <summary>
        /// Releases decoder-owned payloads for conflicts discovered since a collection started.
        /// This is called only after every worker has joined, so no worker local can still retain
        /// the admission payload while another admission reuses its charge.
        /// </summary>
        internal void ReleaseBatchConflicts(long afterSequence)
        {
            lock (gate)
            {
                foreach (Candidate candidate in candidates.Values)
                    if (candidate.Conflicted && candidate.ConflictSequence > afterSequence &&
                        candidate.PayloadReleasableOnConflict)
                        ReleaseAppliedConflict(candidate);
            }
        }

        internal void ReleaseAppliedConflict(ShardHeader header)
        {
            lock (gate)
            {
                if (candidates.TryGetValue((header.FileId, header.Index, header.IsParity),
                    out Candidate? candidate))
                    ReleaseAppliedConflict(candidate);
            }
        }

        private void ReleaseAppliedConflict(Candidate candidate)
        {
            if (!candidate.Conflicted || candidate.Payload is not { } payload)
                return;
            candidate.Payload = null;
            candidate.RetainedBytes -= payload.Length;
            retainedBytes -= payload.Length;
        }

        /// <summary>
        /// Transfers ownership of returned candidates from revocable worker slots to the caller.
        /// A later collection call must keep those payloads charged until its marker has actually
        /// been applied to the caller's retained list.
        /// </summary>
        internal void MarkReturnedExternal(IEnumerable<DecodedShard> returned)
        {
            lock (gate)
            {
                foreach (DecodedShard shard in returned)
                {
                    if (shard.IsTerminalConflict ||
                        !candidates.TryGetValue((shard.Header.FileId, shard.Header.Index,
                            shard.Header.IsParity), out Candidate? candidate) || candidate.Conflicted)
                        continue;
                    candidate.PayloadReleasableOnConflict = false;
                    candidate.Slot = null;
                }
            }
        }

        internal static int RetentionCharge(DecodedShard shard) => checked(
            2 * ShardHeader.Size(shard.Header.FileName) + 2 * shard.SourceFile.Length +
            shard.Payload.Length + PerShardOverheadBytes);

        internal ShardResourceLimitException LimitException() => new(
            $"Decoded shard retention reached DecodeMemoryBudgetMB={byteLimit / 1_000_000:N0} " +
            "or its successful-shard count limit. Split the capture set or raise that setting deliberately.");

        internal bool HasInconsistentFamily
        {
            get { lock (gate) return inconsistentFamilyFileId.HasValue; }
        }

        internal ShardFamilyMismatchException FamilyMismatchException()
        {
            lock (gate)
                return new ShardFamilyMismatchException(
                    $"CRC-valid shards for file {inconsistentFamilyFileId.GetValueOrDefault():x16} " +
                    "contain inconsistent file identity or recovery metadata.");
        }

        internal bool TryPublish(ShardHeader header, SuccessfulShardSlot slot, DecodedShard shard)
        {
            lock (gate)
            {
                var key = (header.FileId, header.Index, header.IsParity);
                if (!candidates.TryGetValue(key, out Candidate? candidate) || candidate.Conflicted ||
                    !ReferenceEquals(candidate.Slot, slot))
                    return false;
                slot.Shard = shard;
                return true;
            }
        }

        private SuccessfulShardAdmission TryAdmitCore(ShardHeader header, ReadOnlySpan<byte> payload,
            int bytes, string sourceFile, int eccParity, int correctedBytes, SuccessfulShardSlot? slot,
            byte[]? ownedPayload, bool payloadReleasableOnConflict)
        {
            lock (gate)
            {
                bool newFamily = !families.TryGetValue(header.FileId, out ShardHeader? family);
                if (!newFamily)
                {
                    if (!family!.HasSameFamilyAs(header))
                    {
                        inconsistentFamilyFileId ??= header.FileId;
                        return new(SuccessfulShardAdmissionKind.InconsistentFamily, null);
                    }
                }

                var key = (header.FileId, header.Index, header.IsParity);
                if (candidates.TryGetValue(key, out Candidate? existing))
                {
                    if (existing.Conflicted)
                        return new(SuccessfulShardAdmissionKind.TerminalConflict, null);
                    if (existing.Header.PayloadLength == header.PayloadLength &&
                        existing.Header.PayloadCrc32 == header.PayloadCrc32 &&
                        existing.Payload is { } existingPayload &&
                        existingPayload.AsSpan().SequenceEqual(payload))
                        return new(SuccessfulShardAdmissionKind.Duplicate, null);

                    existing.Conflicted = true;
                    existing.ConflictSequence = ++conflictSequence;
                    if (existing.Slot is not null)
                        existing.Slot.Shard = null;
                    existing.Slot = null;
                    return new(SuccessfulShardAdmissionKind.Conflict, null);
                }

                if (bytes <= 0 || retainedCount >= countLimit || bytes > byteLimit - retainedBytes)
                {
                    refusedCount++;
                    return new(SuccessfulShardAdmissionKind.Refused, null);
                }
                byte[] retained = ownedPayload ?? payload.ToArray();
                // A refusal must be state-free. In particular, do not retain an attacker-chosen
                // family header/name before its first shard has passed both byte and count admission.
                if (newFamily)
                    families.Add(header.FileId, header);
                candidates.Add(key, new Candidate(header, retained, bytes, sourceFile,
                    eccParity, correctedBytes, slot, payloadReleasableOnConflict));
                retainedBytes += bytes;
                retainedCount++;
                return new(SuccessfulShardAdmissionKind.Added, retained);
            }
        }
    }

    /// <summary>
    /// Run-wide admission for failed-capture fusion material. Worker scratch consumes most of the
    /// configured decode budget, so salvage may retain at most one eighth of that budget and no
    /// more than PhotoFusion's useful capture count for any one layout signature. The lock covers
    /// only failed captures; normal decode work never contends on it.
    /// </summary>
    internal sealed class FailedCaptureRetentionBudget
    {
        private const int SalvageBudgetDivisor = 8;
        // Two-capture cluster fusion peaks below 3x retained bytes per capture: the two captures,
        // recovered stream, compact bucket/frontier/byte tags and one candidate. Charging every
        // retained capture at 3x also covers the cheaper >=3-capture majority path.
        private const int FusionWorkingSetFactor = 3;
        private readonly object gate = new();
        private readonly long byteLimit;
        private readonly Dictionary<(int GridW, int GridH, int Bits, int Ecc, bool Interleave2), int> groupCounts = [];
        private long retainedBytes;
        private long reservedBytes;
        private int refusedCount;

        internal FailedCaptureRetentionBudget(int decodeMemoryBudgetMB)
        {
            byteLimit = checked(decodeMemoryBudgetMB * 1_000_000L / SalvageBudgetDivisor);
        }

        internal int RefusedCount
        {
            get { lock (gate) return refusedCount; }
        }

        internal long RetainedBytes
        {
            get { lock (gate) return retainedBytes; }
        }

        internal long ReservedBytes
        {
            get { lock (gate) return reservedBytes; }
        }

        internal bool TryReserve(Layout layout, int bytes)
        {
            var key = (layout.GridW, layout.GridH, layout.BitsPerCell, layout.EccParity, layout.Interleave2);
            lock (gate)
            {
                groupCounts.TryGetValue(key, out int count);
                long charge = checked((long)bytes * FusionWorkingSetFactor);
                if (bytes <= 0 || count >= PhotoFusion.MaxCapturesPerGroup ||
                    (count == 0 && groupCounts.Count >= PhotoFusion.MaxFusionGroups) ||
                    charge > byteLimit - reservedBytes)
                {
                    refusedCount++;
                    return false;
                }
                groupCounts[key] = count + 1;
                retainedBytes += bytes;
                reservedBytes += charge;
                return true;
            }
        }

        internal void Release(DecodeDiagnostics diagnostics)
        {
            if (diagnostics.CellsLayout is not { } layout || diagnostics.SalvageReservedBytes <= 0)
                return;
            var key = (layout.GridW, layout.GridH, layout.BitsPerCell, layout.EccParity, layout.Interleave2);
            lock (gate)
            {
                retainedBytes -= diagnostics.SalvageReservedBytes;
                reservedBytes -= (long)diagnostics.SalvageReservedBytes * FusionWorkingSetFactor;
                if (groupCounts.TryGetValue(key, out int count))
                {
                    if (count <= 1)
                        groupCounts.Remove(key);
                    else
                        groupCounts[key] = count - 1;
                }
            }
            diagnostics.SalvageReservedBytes = 0;
            diagnostics.Cells = null;
            diagnostics.CellsLayout = null;
        }
    }
}
