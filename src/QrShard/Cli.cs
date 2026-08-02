using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.DependencyInjection;

namespace QrShard;

/// <summary>Command handlers with their dependencies resolved from the composition root.</summary>
internal sealed record CliServices(
    IShardEncoder Encoder, IShardDecoder Decoder, IVideoDecoder VideoDecoder,
    ISlideshowWriter Slideshow, ISelfTest SelfTest, ISessionStore Sessions,
    IParityReassembler Parity, IShardAssembler Assembler, HeatmapRenderer Heatmap, ICalibration Calibration);

/// <summary>Command-line interface, separated from Program for testability.</summary>
internal sealed class Cli(AppSettings? settings = null)
{
    public int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null,
        TextReader? stdin = null, CancellationToken cancellationToken = default)
    {
        var @out = stdout ?? Console.Out;
        var err = stderr ?? Console.Error;
        try
        {
            // Inside the try, not before it. AppSettings.Load raises InvalidOperationException with
            // a message written to be read by a user — "appsettings.json: invalid
            // DecodeMaxParallelism '99999'. Possible values: 0 (auto) to 1024." — and
            // InvalidOperationException is in the Handled list below. Evaluating it one line
            // earlier meant that carefully worded message was delivered as an unhandled crash with
            // a stack trace and a 0xE0434352 exit code that appears in no documentation, which is
            // the opposite of the "fail loudly, naming the setting" contract README states.
            var cfg = settings ?? AppSettings.Current;
            using var provider = ServiceRegistration.BuildProvider(cfg);
            var services = new CliServices(
                provider.GetRequiredService<IShardEncoder>(),
                provider.GetRequiredService<IShardDecoder>(),
                provider.GetRequiredService<IVideoDecoder>(),
                provider.GetRequiredService<ISlideshowWriter>(),
                provider.GetRequiredService<ISelfTest>(),
                provider.GetRequiredService<ISessionStore>(),
                provider.GetRequiredService<IParityReassembler>(),
                provider.GetRequiredService<IShardAssembler>(),
                provider.GetRequiredService<HeatmapRenderer>(),
                provider.GetRequiredService<ICalibration>());
            return RunCore(args, @out, err, stdin ?? Console.In, cfg, services, cancellationToken);
        }
        catch (Exception ex)
        {
            // A bad image decoded under Parallel.For (or the pipelined producer) surfaces wrapped
            // in AggregateException; unwrap so the handlers see the real type. Handle only when
            // EVERY surfaced exception is one we translate — otherwise rethrow, so an unexpected
            // sibling (a real bug) is never masked by a handled one that merely sorted first.
            var inners = ex is AggregateException agg
                ? (IReadOnlyList<Exception>)agg.Flatten().InnerExceptions
                : [ex];
            static bool Handled(Exception e) => e is ShardDecodeException or ArgumentException
                or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException
                or FormatException or OverflowException;
            if (inners.Count > 0 && inners.All(Handled))
            {
                err.WriteLine($"error: {SafeMessage(inners[0].Message)}");
                return 1;
            }
            throw;
        }
    }

    private static int RunCore(string[] args, TextWriter @out, TextWriter err, TextReader stdin,
        AppSettings settings, CliServices services, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help(@out, err);

        // Mirrors the help triple so the two behave alike. Intercepted before the per-command
        // ArgSpec check, which is keyed on a command name and would otherwise reject "--version"
        // as an unknown command.
        if (args[0] is "-v" or "--version" or "version")
            return Version(@out);

        string command = args[0].ToLowerInvariant();
        // Reject unknown/misspelled options up front. Without this, ParseArgs accepts any
        // `-x value` pair blindly, so a typo like `--pasword pw` silently encodes UNENCRYPTED and
        // `--recvery 30` silently yields zero parity — data-exposure/data-loss for an integrity
        // tool. Validated against a per-command allowlist so it can't drift from the handlers.
        if (ArgSpecs.TryGetValue(command, out var spec))
        {
            if (FindDuplicateOption(args[1..], spec) is { } duplicateError)
                return Help(@out, err, duplicateError);
            var (pos, nm, fl) = ParseArgs(args[1..]);
            if (ValidateOptions(spec, nm, fl, pos) is { } optionError)
                return Help(@out, err, optionError);
        }

        switch (command)
        {
            case "encode":
            {
                var (positional, named, flags) = ParseArgs(args[1..]);
                bool video = flags.Contains("--video");
                if (flags.Contains("--open") && flags.Contains("--json"))
                    return Help(@out, err, "--open cannot be combined with --json because JSON mode has no browser-launch output channel.");
                string? slideshowKind = Get(named, "--slideshow");
                if (!video && (flags.Contains("--open") || slideshowKind is not null ||
                               Get(named, "-i", "--interval") is not null))
                    return Help(@out, err, "--open, --slideshow, and -i/--interval require --video.");
                if (slideshowKind is not null &&
                    !slideshowKind.Equals("html", StringComparison.OrdinalIgnoreCase) &&
                    !slideshowKind.Equals("apng", StringComparison.OrdinalIgnoreCase))
                    return Help(@out, err, "--slideshow must be exactly 'html' or 'apng'.");
                if (positional.Count == 0)
                    return Help(@out, err, "encode requires one or more input files or folders.");
                foreach (string p in positional)
                {
                    if (!File.Exists(p) && !Directory.Exists(p))
                        return Help(@out, err, $"not found: {p}");
                    if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                        return Help(@out, err,
                            $"symbolic links and junctions are not accepted as top-level inputs: {p}. " +
                            "Select the link target explicitly.");
                }
                string? encodePassword = ResolvePassword(named, flags, stdin);
                bool json = flags.Contains("--json");
                Action<string> preLog = json ? _ => { } : @out.WriteLine; // keep stdout clean for --json

                // One file → encoded directly. A folder, or more than one input, is tar-ed into a
                // temp archive and encoded as one payload; decoding extracts it back to a directory.
                bool isArchive = positional.Count > 1 || Directory.Exists(positional[0]);
                string input = positional[0];
                string inputName = positional.Count > 1
                    ? "bundle"
                    : Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)));
                string file = input;
                ShardAssembler.TemporaryDirectoryLease? tempTar = null;
                try
                {
                if (isArchive)
                {
                    // This tar is the complete plaintext bundle even when -p will encrypt the
                    // payload. Its temporary root has an unpredictable name and requests 0700 on
                    // Unix or a protected owner-only DACL on Windows.
                    tempTar = CreatePrivateTempDirectory();
                    file = Path.Combine(tempTar.Path, inputName + ".tar");
                    preLog(positional.Count > 1
                        ? $"Archiving {positional.Count} inputs..."
                        : $"Archiving folder '{ShardHeader.Display(input)}'...");
                    WriteTar(positional, file); // may throw on a name collision — finally still cleans up
                }

                Action<string> encLog = preLog;

                // Precedence: flag > --profile preset > appsettings.json EncodeDefaults >
                // built-in default. The camera profile swaps in photo-appropriate density
                // defaults (big cells, few colors, heavy ECC); explicit flags still win.
                var defaults = ResolveProfile(named, settings, out string? profileError);
                if (defaults is null)
                    return Help(@out, err, profileError);
                bool camera = flags.Contains("--camera");
                string resolutionValue = Get(named, "-r", "--resolution") ?? defaults.Resolution;
                var (width, height, resolutionNote) = ResolveResolution(resolutionValue);
                var opt = BuildEncodeOptions(named, flags, defaults, camera, width, height,
                    encodePassword) with { IsArchive = isArchive };
                string outDir = Get(named, "-o", "--out") ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(Path.TrimEndingDirectorySeparator(input)))!,
                    inputName + settings.ShardFolderSuffix);

                if (flags.Contains("--dry-run"))
                    return DryRun(services.Encoder.Plan(file, opt), outDir, json, @out);

                encLog($"Encoding '{ShardHeader.Display(input)}' → {ShardHeader.Display(outDir)}");
                encLog($"  {opt.Width}x{opt.Height}px{resolutionNote}, cell {opt.CellPx}px, {opt.BitsPerCell} bits/cell, " +
                       $"ECC parity {opt.EccParity}, recovery {opt.RecoveryPercent}%, " +
                       $"format {opt.ImageFormat}, compression {(opt.Compress ? "on" : "off")}" +
                       (camera ? ", camera profile (finder patterns)" : ""));
                var result = services.Encoder.Encode(file, outDir, opt, encLog);

                string? slideshowPath = null;
                if (video)
                {
                    int intervalMs = GetInt(named, "-i", "--interval", SlideshowWriter.DefaultIntervalMs);
                    bool apng = string.Equals(Get(named, "--slideshow"), "apng", StringComparison.OrdinalIgnoreCase);
                    slideshowPath = apng
                        ? services.Slideshow.WriteApng(outDir, result.Files, intervalMs)
                        : services.Slideshow.Write(outDir, result.Files, intervalMs);
                }

                if (json)
                {
                    @out.WriteLine(new JsonReports().EncodeReport(result, outDir, slideshowPath));
                    return 0;
                }

                @out.WriteLine($"Done: {result.ImageCount} image(s) of {result.Width}x{result.Height}px, up to {result.BytesPerImage:N0} payload bytes each.");
                if (result.ParityImages > 0)
                    @out.WriteLine(opt.FountainPercent > 0
                        ? $"  {result.DataImages} data + {result.ParityImages} fountain-coded image(s); " +
                          $"any ~{result.StripeData} captured frames per stripe reconstruct the data."
                        : $"  {result.DataImages} data + {result.ParityImages} parity image(s); " +
                          $"can recover up to {result.StripeParity} lost image(s) per {result.StripeData + result.StripeParity}.");
                if (slideshowPath is not null)
                {
                    int intervalMs = GetInt(named, "-i", "--interval", SlideshowWriter.DefaultIntervalMs);
                    @out.WriteLine($"Slideshow: {ShardHeader.Display(slideshowPath)} ({intervalMs} ms/image, ~{result.ImageCount * intervalMs / 1000.0:0.#} s per cycle).");
                    @out.WriteLine(slideshowPath.EndsWith(".apng", StringComparison.OrdinalIgnoreCase)
                        ? "  Open it and record the screen for at least one full cycle."
                        : "  Open it in a browser, click “Start fullscreen playback”, and record at least one full cycle.");
                    if (flags.Contains("--open"))
                        OpenInBrowser(slideshowPath, @out);
                }
                return 0;
                }
                finally
                {
                    tempTar?.Dispose();
                }
            }

            case "decode":
            {
                var (positional, named, dflags) = ParseArgs(args[1..]);
                string? password = ResolvePassword(named, dflags, stdin);
                bool djson = dflags.Contains("--json");
                Action<string> decLog = djson ? _ => { } : @out.WriteLine; // keep stdout clean for --json
                if (dflags.Contains("--clipboard"))
                {
                    if (positional.Count > 0)
                        return Help(@out, err, "--clipboard does not accept image, folder, or recording arguments.");
                    if (dflags.Contains("--watch"))
                        return Help(@out, err, "--clipboard and --watch cannot be combined.");
                    if (Get(named, "--fps") is not null)
                        return Help(@out, err, "--fps applies only to decoding a video or animated-image recording.");
                    return DecodeClipboard(services, Get(named, "--session"), Get(named, "-o", "--out"),
                        password, @out, err, djson, settings.DecodeMemoryBudgetMB);
                }
                if (positional.Count == 0)
                {
                    if (Get(named, "--fps") is not null)
                        return Help(@out, err, "--fps applies only to decoding a video or animated-image recording.");
                    string? savedSession = Get(named, "--session");
                    if (savedSession is not null && !dflags.Contains("--watch"))
                        return DecodeWithSession(services, [], savedSession, Get(named, "-o", "--out"),
                            password, @out, err, djson, settings.DecodeMemoryBudgetMB);
                    return Help(@out, err, "decode requires a folder, image files, a video recording, --session, or --clipboard.");
                }

                if (dflags.Contains("--watch"))
                {
                    if (Get(named, "--fps") is not null)
                        return Help(@out, err, "--fps applies only to decoding a video or animated-image recording, not --watch.");
                    if (positional.Count != 1 || !Directory.Exists(positional[0]))
                        return Help(@out, err, "--watch requires exactly one folder to watch.");
                    return DecodeWatch(services, positional[0], Get(named, "--session"),
                        Get(named, "-o", "--out"), password, @out, err, djson, settings.WatchPollMs,
                        settings.DecodeMemoryBudgetMB, cancellationToken);
                }

                // A single video file (or animated image) is a recording of the slideshow.
                if (positional.Count == 1 && File.Exists(positional[0]) &&
                    (VideoDecoder.IsVideoFile(positional[0]) ||
                     (IsImageFile(positional[0]) && VideoDecoder.IsAnimatedImage(positional[0]))))
                {
                    // --session is in decode's allowlist and the help presented it as a general
                    // decode option, but this branch returns before sessionPath is ever read, so
                    // the option was parsed, validated and thrown away. A user decoding a
                    // recording of an incomplete transfer got no partial progress and no warning
                    // that the flag they passed to preserve it did nothing — the one situation the
                    // flag exists for. Say so instead of pretending.
                    if (Get(named, "--session") is not null)
                        return Help(@out, err,
                            "--session applies to decoding image files, not a recording: a recording is " +
                            "re-read from the start each time, so there is no partial state to carry. " +
                            "Extract frames to images first if you need to resume.");

                    double fps = GetValidatedFps(named, 8.0);
                    decLog($"Decoding video '{ShardHeader.Display(positional[0])}' (extracting at {fps} fps)...");
                    // Escalate fps automatically for file recordings unless the user pinned --fps.
                    bool userSetFps = Get(named, "--fps") is not null;
                    var fromVideo = services.VideoDecoder.Decode(positional[0], Get(named, "-o", "--out"), fps,
                        decLog, out _, password, decodeWorkers: 1, escalateFps: !userSetFps);
                    return ReportRestored(@out, fromVideo, djson);
                }

                if (Get(named, "--fps") is not null)
                    return Help(@out, err,
                        "--fps applies only when the input is one video or animated-image recording.");

                foreach (string p in positional)
                {
                    if (!Directory.Exists(p) && !File.Exists(p))
                        return Help(@out, err, $"not found: {p}");
                }
                var images = ShardDecoder.MaterializeInputPaths(
                    EnumerateImageArguments(positional), settings.DecodeMemoryBudgetMB);
                if (images.Count == 0)
                    return Help(@out, err, "no image files found to decode.");

                string? sessionPath = Get(named, "--session");
                if (sessionPath is not null)
                    return DecodeWithSession(services, images, sessionPath, Get(named, "-o", "--out"),
                        password, @out, err, djson, settings.DecodeMemoryBudgetMB);

                decLog($"Decoding {images.Count} image(s)...");
                var shards = services.Decoder.CollectShards(images, decLog);
                if (shards.Count == 0)
                {
                    err.WriteLine("error: no decodable shard images were found.");
                    return 1;
                }
                try
                {
                    // Assemble restores each complete file (writing them out as it goes) and throws
                    // on the first that can't be reassembled — so a folder mixing a complete file
                    // with an incomplete one still yields the complete one on disk.
                    var restored = services.Assembler.Assemble(shards, Get(named, "-o", "--out"), decLog, password);
                    return ReportRestored(@out, restored, djson);
                }
                catch (ShardDecodeException ex)
                {
                    // Distinguish "whole images missing/unreadable" (recoverable by capturing more)
                    // from a complete-but-corrupt set (genuine data corruption). Only the former is
                    // nudged toward the resumable flow with the documented incomplete exit code.
                    if (services.Parity.IsSetComplete(shards))
                    {
                        err.WriteLine($"error: {SafeMessage(ex.Message)}");
                        return 1;
                    }
                    if (djson)
                    {
                        @out.WriteLine(new JsonReports().DecodeIncompleteReport(shards, services.Parity));
                        return 3;
                    }
                    PrintSetStatus(@out.WriteLine, shards, services.Parity);
                    // The underlying message, always. This branch assumed the failure was always
                    // "images are missing", so decoding a mixed folder with -o reported "capture
                    // the missing images" when the actual fault was "omit -o and decode them
                    // separately" — advice that cannot fix it, for a set that was not incomplete.
                    err.WriteLine($"error: {SafeMessage(ex.Message)}");
                    @out.WriteLine("Incomplete — some images are missing or unreadable. Capture them and decode again, or:");
                    @out.WriteLine("  • add --session <file> to accumulate captures across sittings (resumes from what you have),");
                    @out.WriteLine("  • or --watch to decode images as they land and finish automatically.");
                    return 3;
                }
            }

            case "verify":
            {
                var (positional, named, vflags) = ParseArgs(args[1..]);
                bool json = vflags.Contains("--json");
                foreach (string p in positional)
                {
                    if (!Directory.Exists(p) && !File.Exists(p))
                        return Help(@out, err, $"not found: {p}");
                }
                var images = ShardDecoder.MaterializeInputPaths(
                    EnumerateImageArguments(positional), settings.DecodeMemoryBudgetMB);
                string? session = Get(named, "--session");
                if (images.Count == 0 && session is null)
                    return Help(@out, err, "verify requires a folder, image files, or --session.");

                if (session is not null)
                    ValidateSessionPathAliases(session, images, outputPath: null);
                using ISessionTransaction? transaction = session is null
                    ? null
                    : services.Sessions.Open(session);
                if (transaction is not null)
                    ReportSessionRecovery(transaction, err);

                var successful = new ShardDecoder.SuccessfulShardRetentionBudget(
                    settings.DecodeMemoryBudgetMB);
                successful.Seed(transaction?.Shards ?? []);
                List<DecodedShard> collected = images.Count > 0
                    ? services.Decoder.CollectShards(images,
                        json ? _ => { } : @out.WriteLine, successful)
                    : [];
                var markerKeys = collected.Where(static s => s.IsTerminalConflict)
                    .Select(static s => (s.Header.FileId, s.Header.Index, s.Header.IsParity))
                    .ToHashSet();
                int newMarkerConflicts = transaction is null
                    ? markerKeys.Count
                    : collected.Where(static s => s.IsTerminalConflict)
                        .Count(s => !transaction.IsConflicted(s));
                var shards = transaction?.Shards.ToList() ?? [];
                if (markerKeys.Count > 0)
                    shards.RemoveAll(s => markerKeys.Contains(
                        (s.Header.FileId, s.Header.Index, s.Header.IsParity)));
                var candidates = collected.Where(s => !s.IsTerminalConflict &&
                    !markerKeys.Contains((s.Header.FileId, s.Header.Index, s.Header.IsParity)) &&
                    (transaction is null || !transaction.IsConflicted(s))).ToList();
                shards = MergeShards(shards, candidates, out int currentConflicts);
                int terminalConflicts = (transaction?.ConflictedShardCount ?? 0) +
                    newMarkerConflicts + currentConflicts;
                if (shards.Count == 0 && terminalConflicts == 0)
                {
                    err.WriteLine("error: no decodable shards found.");
                    return 1;
                }

                // An incomplete set is not an error — it is the answer verify exists to give, and it
                // is fixed by capturing more, so it gets decode's incomplete code rather than 1.
                // Exit 1 stays reserved for "these images are unusable" (no decodable shards above),
                // which is the one outcome a script must not retry by capturing more.
                bool complete = services.Parity.IsSetComplete(shards);
                if (json)
                {
                    @out.WriteLine(new JsonReports().VerifyReport(shards, services.Parity,
                        terminalConflicts));
                    return complete ? 0 : 3;
                }
                PrintSetStatus(@out.WriteLine, shards, services.Parity);
                if (terminalConflicts > 0)
                    @out.WriteLine($"Terminal conflicts: {terminalConflicts:N0} ordinal(s) are treated as missing.");
                @out.WriteLine(complete
                    ? "Complete: every file can be fully reassembled."
                    : "Incomplete: capture the missing images and verify again.");
                return complete ? 0 : 3;
            }

            case "info":
            {
                var (positional, named, iflags) = ParseArgs(args[1..]);
                if (positional.Count != 1 || !File.Exists(positional[0]))
                    return Help(@out, err, "info requires one shard image.");
                bool json = iflags.Contains("--json");
                DecodedShard shard;
                string? heatmapPath = Get(named, "--heatmap");
                string? qualityPath = Get(named, "--quality-heatmap");
                string? renderedHeatmap = null;
                int correctedCw = 0, failedCw = 0;
                if (heatmapPath is not null || qualityPath is not null)
                {
                    var diag = services.Decoder.Diagnose(positional[0]);
                    if (diag.Layout is null)
                    {
                        err.WriteLine($"error: cannot render heatmap: {SafeMessage(diag.Error ?? "unknown diagnostic failure")}");
                        return 1; // frame never located — nothing to map
                    }

                    // --heatmap prefers the ECC-correction map; when the decode did not run RS (no
                    // ECC, or it failed before decoding) it falls back to the capture-quality map so
                    // a FAILED capture still shows where it went wrong.
                    if (heatmapPath is not null)
                    {
                        if (diag.Layout.EccParity > 0 && diag.CodewordErrors.Length > 0)
                        {
                            services.Heatmap.Render(diag.Layout, diag.CodewordErrors, heatmapPath);
                            correctedCw = diag.CodewordErrors.Count(e => e > 0);
                            failedCw = diag.CodewordErrors.Count(e => e < 0);
                            if (!json)
                                @out.WriteLine($"heatmap   : {ShardHeader.Display(heatmapPath)} ({correctedCw} codeword(s) needed correction, {failedCw} beyond correction)");
                        }
                        else if (diag.CellMargins is not null)
                        {
                            services.Heatmap.RenderQuality(diag.Layout, diag.CellMargins, heatmapPath);
                            if (!json)
                            {
                                string why = diag.Layout.EccParity == 0 ? "no ECC in this image" : "the decode did not complete";
                                @out.WriteLine($"heatmap   : {ShardHeader.Display(heatmapPath)} (capture-quality map — {why})");
                            }
                        }
                        else
                        {
                            err.WriteLine("error: cannot render heatmap for this image.");
                        }
                        renderedHeatmap = heatmapPath;
                    }
                    if (qualityPath is not null && diag.CellMargins is not null)
                    {
                        services.Heatmap.RenderQuality(diag.Layout, diag.CellMargins, qualityPath);
                        renderedHeatmap ??= qualityPath;
                        if (!json)
                            @out.WriteLine($"quality   : {ShardHeader.Display(qualityPath)} (green = confident classification, red = ambiguous/likely wrong)");
                    }

                    if (diag.Shard is null)
                    {
                        err.WriteLine($"error: {SafeMessage(diag.Error ?? "unknown diagnostic failure")}");
                        return 1;
                    }
                    shard = diag.Shard;
                }
                else
                {
                    shard = services.Decoder.DecodeImage(positional[0], new DecodeScratch());
                }
                if (json)
                {
                    @out.WriteLine(new JsonReports().InfoReport(shard, renderedHeatmap, correctedCw, failedCw));
                    return 0;
                }
                var h = shard.Header;
                @out.WriteLine($"file      : {ShardHeader.Display(h.FileName)}");
                @out.WriteLine($"file id   : {h.FileId:X16}");
                @out.WriteLine($"part      : {(h.IsParity ? $"parity #{(long)h.Index + 1}" : $"{(long)h.Index + 1} of {h.Count}")}");
                @out.WriteLine($"payload   : {h.PayloadLength:N0} bytes (CRC-32 verified)");
                if (h.StripeParity > 0)
                    @out.WriteLine($"recovery  : {h.StripeParity} parity per {h.StripeData} data images per stripe");
                @out.WriteLine($"ecc       : {(shard.EccParity > 0 ? $"RS parity {shard.EccParity}, corrected {shard.CorrectedBytes} byte(s)" : "none")}");
                // The label has to follow FlagBrotli. Hardcoding "deflate" mislabelled every
                // compressed shard a current encoder produces, since Brotli is the default.
                string codec = (h.Flags & ShardHeader.FlagBrotli) != 0 ? "brotli" : "deflate";
                @out.WriteLine($"original  : {h.OriginalLength:N0} bytes{((h.Flags & ShardHeader.FlagCompressed) != 0 ? $", {codec}-compressed to {h.TotalLength:N0}" : "")}");
                @out.WriteLine($"sha-256   : {Convert.ToHexStringLower(h.Sha256)}");
                return 0;
            }

            case "send":
                // One-step sender: encode with a slideshow and open it in the default browser.
                return RunCore(["encode", .. args[1..], "--video", "--open"], @out, err, stdin,
                    settings, services, cancellationToken);

            case "receive":
            {
                var (positional, named, rflags) = ParseArgs(args[1..]);
                if (positional.Count > 0)
                    return Help(@out, err, "receive does not accept positional arguments.");
                bool screen = rflags.Contains("--screen");
                if (screen && Get(named, "--device") is not null)
                    return Help(@out, err, "--device cannot be combined with --screen.");
                if (screen && Get(named, "--format") is not null)
                    return Help(@out, err, "--format applies to a camera/capture device, not --screen.");
                if (!screen && Get(named, "--region") is not null)
                    return Help(@out, err, "--region requires --screen.");
                string? receivePassword = ResolvePassword(named, rflags, stdin);
                double fps = GetValidatedFps(named, settings.ReceiveFps, max: 120);
                IFrameSource source;
                string sourceLabel;
                if (screen)
                {
                    // Self-capture: decode this machine's own screen — put the sender's
                    // slideshow anywhere visible, including inside an RDP/VM window.
                    source = new ScreenFrameSource(ScreenFrameSource.ParseRegion(Get(named, "--region")),
                        settings.DecodeMemoryBudgetMB, settings.FfmpegPath);
                    sourceLabel = "screen";
                    @out.WriteLine("Receiving from this machine's screen — put the sender's slideshow somewhere visible (an RDP or VM window works).");
                }
                else
                {
                    string? device = Get(named, "--device") ?? LiveFrameSource.DefaultDevice();
                    if (device is null)
                        return Help(@out, err,
                            "receive on Windows needs --device \"<webcam name>\" or --screen (list devices with: ffmpeg -list_devices true -f dshow -i dummy)");
                    source = new LiveFrameSource(Get(named, "--format"), settings.DecodeMemoryBudgetMB,
                        settings.FfmpegPath);
                    sourceLabel = device;
                    @out.WriteLine($"Receiving from '{ShardHeader.Display(device)}' — point the camera at the sender's slideshow.");
                }
                int workers = settings.ReceiveDecodeWorkers > 0
                    ? settings.ReceiveDecodeWorkers
                    : Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
                @out.WriteLine($"Decoding at {fps} fps with {workers} worker(s); stops automatically when the transfer completes.");

                var live = new VideoDecoder(services.Decoder, source,
                    services.Assembler, services.Parity, new CameraRectifier(), settings);
                var received = live.Decode(sourceLabel, Get(named, "-o", "--out"), fps, @out.WriteLine, out var liveStats,
                    receivePassword, workers);
                @out.WriteLine($"Restored {received.Count} file(s) after examining {liveStats.FramesExamined} frame(s).");
                return 0;
            }

            case "calibrate":
            {
                var (positional, named, cflags) = ParseArgs(args[1..]);
                if (positional.Count == 1 && Directory.Exists(positional[0]))
                {
                    if (cflags.Contains("--camera") || Get(named, "-o", "--out") is not null ||
                        Get(named, "-r", "--resolution") is not null)
                        return Help(@out, err,
                            "--camera, -o/--out and -r/--resolution generate probes and cannot be used when analyzing a captured folder.");
                    return services.Calibration.Analyze(positional[0], @out);
                }
                if (positional.Count != 0)
                    return Help(@out, err, "calibrate takes no arguments (generate) or one captured folder (analyze).");
                var (width, height, note) = ResolveResolution(Get(named, "-r", "--resolution") ?? "auto");
                string outDir = Get(named, "-o", "--out") ?? Path.Combine(Environment.CurrentDirectory, "qrshard-calibration");
                if (note.Length > 0)
                    @out.WriteLine($"Resolution {width}x{height}{note}");
                return services.Calibration.Generate(outDir, width, height, cflags.Contains("--camera"), @out);
            }

            case "test":
            {
                var (positional, named, tflags) = ParseArgs(args[1..]);
                if (positional.Count == 0)
                {
                    if (named.Count > 0 || tflags.Count > 0)
                        return Help(@out, err,
                            "encode settings apply only to 'test <file>'; the built-in self-test takes no options.");
                    return services.SelfTest.Run() ? 0 : 1; // built-in fixed-fixture self-test
                }
                if (positional.Count != 1 || !File.Exists(positional[0]))
                    return Help(@out, err, "test takes no arguments (built-in self-test) or one file to round-trip at your settings.");
                var tDefaults = ResolveProfile(named, settings, out string? tProfileError);
                if (tDefaults is null)
                    return Help(@out, err, tProfileError);
                bool camera = tflags.Contains("--camera");
                var (width, height, _) = ResolveResolution(Get(named, "-r", "--resolution") ?? tDefaults.Resolution);
                var opt = BuildEncodeOptions(named, tflags, tDefaults, camera, width, height,
                    ResolvePassword(named, tflags, stdin));
                return services.SelfTest.RunFile(positional[0], opt, @out);
            }

            default:
                return Help(@out, err, $"unknown command: {args[0]}");
        }
    }

    /// <summary>
    /// Session decode: merge previously collected shards with this run's, assemble if the set
    /// is now complete (deleting the session), otherwise persist the union and report what is
    /// still missing. Exit code 3 = valid but incomplete.
    /// </summary>
    private static int DecodeWithSession(CliServices services, List<string> images, string sessionPath,
        string? outputPath, string? password, TextWriter @out, TextWriter err, bool json,
        int decodeMemoryBudgetMB)
    {
        Action<string> log = json ? _ => { } : @out.WriteLine;
        ValidateSessionPathAliases(sessionPath, images, outputPath);
        using var transaction = services.Sessions.Open(sessionPath);
        ReportSessionRecovery(transaction, err);
        var known = transaction.Shards.ToList();
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB);
        successful.Seed(known);
        int priorTerminalConflicts = transaction.ConflictedShardCount;
        if (known.Count > 0)
            log($"  session: resuming with {known.Count} previously collected shard(s)");

        if (images.Count > 0)
            log($"Decoding {images.Count} image(s)...");
        var collected = images.Count > 0
            ? services.Decoder.CollectShards(images, log, successful)
            : [];
        // Persist CRC-valid additions before attempting output/decryption. A wrong password, full
        // disk or output-permission failure must not make the user recapture the final image.
        // Save([]) also repairs a previously recovered torn tail before successful deletion.
        transaction.Save(collected);
        var merged = transaction.Shards.ToList();
        int terminalConflicts = transaction.ConflictedShardCount;
        if (merged.Count == 0 && terminalConflicts == 0)
            throw new ShardDecodeException("No decodable shard images were found.");
        ReportTerminalConflicts(transaction, priorTerminalConflicts, err);

        if (services.Parity.IsSetComplete(merged))
        {
            ValidateSessionPathAliases(sessionPath, images, outputPath);
            var restored = services.Assembler.Assemble(merged, outputPath, log, password);
            transaction.Delete();
            return ReportRestored(@out, restored, json);
        }

        if (json)
        {
            @out.WriteLine(new JsonReports().DecodeIncompleteReport(merged, services.Parity,
                terminalConflicts));
            return 3;
        }
        PrintSetStatus(@out.WriteLine, merged, services.Parity);
        @out.WriteLine($"Set incomplete — {merged.Count} shard(s) saved to {ShardHeader.Display(sessionPath)}; capture the missing images and decode again with --session.");
        return 3;
    }

    /// <summary>
    /// Clipboard decode (Windows): read the bitmap off the clipboard — Win+Shift+S a displayed
    /// shard, no file saving — merge it with the session, assemble when complete.
    /// </summary>
    private static int DecodeClipboard(CliServices services, string? sessionPath, string? outputPath,
        string? password, TextWriter @out, TextWriter err, bool json, int decodeMemoryBudgetMB)
    {
        Action<string> log = json ? _ => { } : @out.WriteLine;
        if (sessionPath is not null)
            ValidateSessionPathAliases(sessionPath, [], outputPath);
        if (!OperatingSystem.IsWindows())
        {
            err.WriteLine("error: --clipboard is only supported on Windows.");
            return 1;
        }
        var bmp = new ClipboardReader().TryRead(decodeMemoryBudgetMB);
        if (bmp is null)
        {
            err.WriteLine("error: no bitmap on the clipboard (screenshot a displayed shard first).");
            return 1;
        }

        var shard = services.Decoder.DecodeBitmap(bmp, new DecodeScratch(), "clipboard");
        string which = shard.Header.IsParity
            ? $"parity #{(long)shard.Header.Index + 1}"
            : $"part {(long)shard.Header.Index + 1}/{shard.Header.Count}";
        log($"  ok      clipboard  ({which}, {shard.Payload.Length:N0} bytes)");

        using ISessionTransaction? transaction = sessionPath is not null
            ? services.Sessions.Open(sessionPath)
            : null;
        if (transaction is not null)
            ReportSessionRecovery(transaction, err);
        int priorTerminalConflicts = transaction?.ConflictedShardCount ?? 0;
        // Retain the final validated capture even when assembly/decryption cannot yet publish.
        transaction?.Save([shard]);
        var merged = transaction is null
            ? MergeShards([], [shard], out _)
            : transaction.Shards.ToList();
        if (transaction is not null)
            ReportTerminalConflicts(transaction, priorTerminalConflicts, err);
        if (services.Parity.IsSetComplete(merged))
        {
            if (sessionPath is not null)
                ValidateSessionPathAliases(sessionPath, [], outputPath);
            var restored = services.Assembler.Assemble(merged, outputPath, log, password);
            transaction?.Delete();
            return ReportRestored(@out, restored, json);
        }
        if (json)
        {
            @out.WriteLine(new JsonReports().DecodeIncompleteReport(merged, services.Parity,
                transaction?.ConflictedShardCount ?? 0));
            return 3;
        }
        if (sessionPath is null)
        {
            @out.WriteLine("Set incomplete — use --session <file> to accumulate clipboard captures across screenshots.");
            return 3;
        }
        PrintSetStatus(@out.WriteLine, merged, services.Parity);
        @out.WriteLine($"Set incomplete — {merged.Count} shard(s) saved to {ShardHeader.Display(sessionPath)}; screenshot the next image and run again.");
        return 3;
    }

    /// <summary>
    /// Tars files and/or folders into one archive. A single folder is flattened to the archive
    /// root (its contents extract directly, matching the original folder-encode behavior); with
    /// multiple inputs each folder keeps its own name as a prefix so their trees cannot collide.
    /// Distinct inputs that would land at the same archive path (e.g. two loose files with the
    /// same name from different folders) are refused rather than silently overwritten — this is
    /// an integrity tool; losing a file without a word is the one thing it must never do.
    /// </summary>
    internal static void WriteTar(IReadOnlyList<string> inputs, string tarPath)
    {
        bool prefixFolders = inputs.Count > 1;
        var entries = new List<(string Source, string Name, bool IsDirectory)>();
        void AddEntry(string source, string name, bool isDirectory)
        {
            if (entries.Count >= ShardAssembler.MaxArchiveEntries)
                throw new ArgumentException(
                    $"Folder contains more than {ShardAssembler.MaxArchiveEntries:N0} files/directories; split it into smaller transfers.");
            if (name.Split('/').Length > ShardAssembler.MaxArchiveDepth)
                throw new ArgumentException(
                    $"Input path '{name}' exceeds the maximum archive depth of {ShardAssembler.MaxArchiveDepth}.");
            entries.Add((source, name, isDirectory));
        }
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                string prefix = prefixFolders ? Path.GetFileName(root) + "/" : "";
                if (prefixFolders)
                    AddEntry(root, prefix.TrimEnd('/'), true);
                foreach (string directory in EnumerateArchiveDirectories(input))
                {
                    string rel = Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
                    AddEntry(directory, prefix + rel, true);
                }
                foreach (string f in EnumerateArchiveFiles(input))
                {
                    string rel = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                    AddEntry(f, prefix + rel, false);
                }
            }
            else
            {
                AddEntry(input, Path.GetFileName(input), false);
            }
        }

        var portableEntries = new List<(string Source, string Name, bool IsDirectory, string Key)>(entries.Count);
        foreach (var entry in entries)
        {
            if (!TryCanonicalizeArchivePath(entry.Name, unicodeCanonicalizationAvailable: null,
                    out string normalizedName, out string collisionKey))
                throw new ArgumentException(
                    $"Input maps to non-portable archive path '{ShardHeader.Display(entry.Name)}', or this runtime " +
                    "cannot safely normalize its Unicode spelling. Rename it before encoding.");
            portableEntries.Add((entry.Source, normalizedName, entry.IsDirectory, collisionKey));
        }

        // The archive must restore on case-insensitive and Unicode-normalizing filesystems too.
        // Refuse aliases at encode time rather than creating an archive our decoder must reject.
        var collision = portableEntries
            .GroupBy(e => e.Key, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (collision is not null)
            throw new ArgumentException(
                $"Two inputs map to the same archive path under portable comparison '{collision.First().Name}'; rename one or place them in separate folders " +
                "(a folder input keeps its subtree, so files with the same name in different subfolders are fine).");

        // The intermediate tar is plaintext even when the transfer will subsequently be encrypted.
        // Create it atomically with owner-only permissions inside the random temporary directory.
        using var fs = ShardAssembler.CreatePrivateStagingFile(tarPath);
        using var writer = new System.Formats.Tar.TarWriter(fs, System.Formats.Tar.TarEntryFormat.Pax);
        foreach (var (source, name, isDirectory, _) in portableEntries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (isDirectory)
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, name));
            else
            {
                // TarWriter.WriteEntry(path, name) preserves hard-link identity: the second path
                // becomes a HardLink entry. QrShard intentionally accepts only regular files and
                // directories on extraction, so such an archive could not round-trip. Supplying a
                // regular entry and stream explicitly copies each selected path's bytes instead.
                using var data = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 16, FileOptions.SequentialScan);
                var entry = new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile, name)
                {
                    DataStream = data,
                    ModificationTime = File.GetLastWriteTimeUtc(source),
                };
                if (!OperatingSystem.IsWindows())
                    entry.Mode = File.GetUnixFileMode(source) & ShardAssembler.PortableUnixFileModeMask;
                writer.WriteEntry(entry);
            }
        }
    }

    /// <summary>
    /// Normalizes every archive path segment and builds the portable collision key using the same
    /// policy as extraction. The nullable globalization override exists only for deterministic
    /// environment-policy tests; production probes the active runtime.
    /// </summary>
    internal static bool TryCanonicalizeArchivePath(string name, bool? unicodeCanonicalizationAvailable,
        out string normalizedName, out string collisionKey)
    {
        string[] segments = name.Split('/');
        var normalized = new string[segments.Length];
        var keys = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (!ShardAssembler.IsSafePathSegment(segment))
            {
                normalizedName = "";
                collisionKey = "";
                return false;
            }
            bool canonical = unicodeCanonicalizationAvailable is bool available
                ? ShardAssembler.TryCanonicalizePortableArchiveSegment(
                    segment, available, out normalized[i], out keys[i])
                : ShardAssembler.TryCanonicalizePortableArchiveSegment(
                    segment, out normalized[i], out keys[i]);
            if (!canonical || !ShardAssembler.IsSafePathSegment(normalized[i]))
            {
                normalizedName = "";
                collisionKey = "";
                return false;
            }
        }
        normalizedName = string.Join('/', normalized);
        collisionKey = string.Join('/', keys);
        return true;
    }

    internal static ShardAssembler.TemporaryDirectoryLease CreatePrivateTempDirectory() =>
        ShardAssembler.CreatePrivateTemporaryDirectory("qrshard-tar-");

    /// <summary>
    /// Enumerates a selected folder without following reparse points. SearchOption.AllDirectories
    /// follows directory symlinks/junctions: a folder encode could therefore disclose files
    /// outside the selected tree or recurse forever through a link loop. Skip only reparse points
    /// so ordinary hidden/system files remain part of the archive.
    /// </summary>
    internal static IEnumerable<string> EnumerateArchiveFiles(string root) =>
        Directory.EnumerateFiles(root, "*", ArchiveEnumerationOptions());

    internal static IEnumerable<string> EnumerateArchiveDirectories(string root) =>
        Directory.EnumerateDirectories(root, "*", ArchiveEnumerationOptions());

    private static EnumerationOptions ArchiveEnumerationOptions() =>
        new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

    /// <summary>Opens the slideshow in the platform's default browser (suppressed by the
    /// QRSHARD_NO_LAUNCH environment variable, e.g. in tests and scripts).</summary>
    private static void OpenInBrowser(string path, TextWriter @out)
    {
        if (Environment.GetEnvironmentVariable("QRSHARD_NO_LAUNCH") is not null)
        {
            @out.WriteLine("  (browser launch suppressed by QRSHARD_NO_LAUNCH)");
            return;
        }
        string canonicalPath = Path.GetFullPath(path);
        try
        {
            System.Diagnostics.ProcessStartInfo? start = BuildBrowserStartInfo(
                canonicalPath, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
            if (start is null)
            {
                @out.WriteLine($"  Could not find a trusted browser launcher; open {ShardHeader.Display(canonicalPath)} manually.");
                return;
            }
            System.Diagnostics.Process.Start(start);
            @out.WriteLine("  Opened the slideshow in your default browser — click “Start fullscreen playback”.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
                                   FileNotFoundException or UnauthorizedAccessException)
        {
            @out.WriteLine($"  Could not launch a browser automatically; open {ShardHeader.Display(canonicalPath)} manually.");
        }
    }

    /// <summary>
    /// Constructs a launcher without ever passing a bare executable name to Process.Start.
    /// Unix desktop launchers are resolved to absolute executables; macOS uses the fixed system
    /// launcher. The resolver parameter exists so this security boundary can be tested on every OS.
    /// </summary>
    internal static System.Diagnostics.ProcessStartInfo? BuildBrowserStartInfo(string path,
        bool isWindows, bool isMacOS, Func<string, string?, string?>? resolve = null)
    {
        string canonicalPath = Path.GetFullPath(path);
        if (isWindows)
            return new System.Diagnostics.ProcessStartInfo(canonicalPath) { UseShellExecute = true };

        resolve ??= ExternalToolResolver.Resolve;
        string tool = isMacOS ? "open" : "xdg-open";
        string? executable = resolve(tool, isMacOS ? "/usr/bin/open" : null);
        if (executable is null)
            return null;

        var start = ExternalToolResolver.CreateStartInfo(executable);
        start.ArgumentList.Add(canonicalPath);
        return start;
    }

    /// <summary>
    /// Watch mode: poll a folder for new captures, decode each as it lands (with a settle
    /// delay so half-written screenshots are left for the next poll), and assemble the moment
    /// the set completes. Ctrl+C stops the watch, persisting progress when a session is given.
    /// </summary>
    private static int DecodeWatch(CliServices services, string folder, string? sessionPath,
        string? outputPath, string? password, TextWriter @out, TextWriter err, bool json,
        int pollMs = 250, int decodeMemoryBudgetMB = 4000,
        CancellationToken cancellationToken = default)
    {
        Action<string> log = json ? _ => { } : @out.WriteLine;
        if (sessionPath is not null)
            ValidateSessionPathAliases(sessionPath, [folder], outputPath);
        using ISessionTransaction? transaction = sessionPath is not null
            ? services.Sessions.Open(sessionPath)
            : null;
        if (transaction is not null)
            ReportSessionRecovery(transaction, err);
        var shards = transaction is not null ? transaction.Shards.ToList() : [];
        var successful = new ShardDecoder.SuccessfulShardRetentionBudget(decodeMemoryBudgetMB);
        successful.Seed(shards);
        if (shards.Count > 0)
            log($"  session: resuming with {shards.Count} previously collected shard(s)");
        var seen = shards.ToDictionary(
            s => (s.Header.FileId, s.Header.Index, s.Header.IsParity),
            static s => (DecodedShard?)s);
        var noSessionTerminalKeys = new HashSet<(ulong FileId, int Index, bool Parity)>();
        var families = FamilyMap(shards);
        if (shards.Count > 0 && services.Parity.IsSetComplete(shards))
        {
            if (sessionPath is not null)
                ValidateSessionPathAliases(sessionPath, [folder], outputPath);
            var restored = services.Assembler.Assemble(shards, outputPath, log, password);
            transaction?.Delete();
            return ReportRestored(@out, restored, json);
        }
        // path -> the write time we last ATTEMPTED. Keyed on the timestamp rather than the path
        // alone so a capture that is rewritten gets another go, while one that simply cannot
        // decode is not re-read on every poll forever.
        StringComparer pathComparer = ShardDecoder.FileSystemPathComparer;
        var attempted = new Dictionary<string, DateTime>(pathComparer);
        var currentSet = new HashSet<string>(pathComparer);
        var stalePaths = new List<string>();
        log($"Watching {ShardHeader.Display(folder)} — drop captures in; Ctrl+C stops" +
            (sessionPath is not null ? " (progress persists to the session)." : "."));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                long scanStarted = Stopwatch.GetTimestamp();
                var settled = DateTime.UtcNow - TimeSpan.FromMilliseconds(500);
                // Bound the complete directory view before either sorting it or copying names
                // into the long-lived attempted map. Deleted captures are pruned every poll, so
                // churn cannot make attempted grow for the lifetime of the watcher.
                var current = ShardDecoder.MaterializeInputPaths(
                    Directory.EnumerateFiles(folder).Where(IsImageFile), decodeMemoryBudgetMB);
                currentSet.Clear();
                currentSet.UnionWith(current);
                stalePaths.Clear();
                foreach (string oldPath in attempted.Keys)
                    if (!currentSet.Contains(oldPath))
                        stalePaths.Add(oldPath);
                foreach (string oldPath in stalePaths)
                    attempted.Remove(oldPath);
                var fresh = new List<string>();
                foreach (string path in current)
                {
                    DateTime writeTime = File.GetLastWriteTimeUtc(path);
                    if (writeTime < settled &&
                        !(attempted.TryGetValue(path, out DateTime last) && last == writeTime))
                        fresh.Add(path);
                }
                TimeSpan scanElapsed = Stopwatch.GetElapsedTime(scanStarted);
                if (fresh.Count > 0)
                {
                    if (sessionPath is not null)
                        ValidateSessionPathAliases(sessionPath, fresh, outputPath);
                    // Record the write time we are about to attempt, BEFORE decoding, so a file
                    // is not re-read on every poll — but keyed on that timestamp rather than on
                    // the path, which is what the old blacklist got wrong. A capture still being
                    // written when the 500 ms settle window elapsed, or one the user re-saved with
                    // a better shot, changes its write time and so gets another attempt; a file
                    // that simply cannot decode keeps its time and is left alone. In watch mode
                    // captures keep arriving AND improving, so "attempted at this version" is the
                    // right memory where "seen this path" was not.
                    foreach (string f in fresh)
                        attempted[f] = File.GetLastWriteTimeUtc(f);

                    List<DecodedShard> decoded = services.Decoder.CollectShards(fresh, log, successful);
                    bool changed = false;
                    if (transaction is not null && decoded.Count > 0)
                    {
                        // The session journal is the source of truth for conflicts. Save the whole
                        // validated batch first so two differing copies become a durable terminal
                        // erasure, then rebuild live state from the committed transaction view.
                        int priorConflicts = transaction.ConflictedShardCount;
                        transaction.Save(decoded);
                        shards = transaction.Shards.ToList();
                        seen = shards.ToDictionary(
                            s => (s.Header.FileId, s.Header.Index, s.Header.IsParity),
                            static s => (DecodedShard?)s);
                        families = FamilyMap(shards);
                        successful.ReleasePersistedConflicts(decoded);
                        ReportTerminalConflicts(transaction, priorConflicts, err);
                        changed = true;
                    }
                    else if (transaction is null)
                    {
                        var nextFamilies = new Dictionary<ulong, ShardHeader>(families);
                        foreach (DecodedShard s in decoded)
                        {
                            AddOrValidateFamily(nextFamilies, s.Header);
                            var key = (s.Header.FileId, s.Header.Index, s.Header.IsParity);
                            if (s.IsTerminalConflict)
                            {
                                if (seen.TryGetValue(key, out DecodedShard? conflictedPrior) &&
                                    conflictedPrior is not null)
                                    shards.Remove(conflictedPrior);
                                seen[key] = null;
                                if (noSessionTerminalKeys.Add(key))
                                    err.WriteLine($"warning: conflicting CRC-valid copies made " +
                                        $"ordinal {(long)s.Header.Index + 1} for file {s.Header.FileId:x16} " +
                                        "a terminal erasure for this watch run.");
                                changed = true;
                                continue;
                            }
                            if (seen.TryGetValue(key, out DecodedShard? existing))
                            {
                                if (existing is null || SameShard(existing, s))
                                    continue;
                                shards.Remove(existing);
                                seen[key] = null;
                                if (noSessionTerminalKeys.Add(key))
                                    err.WriteLine($"warning: conflicting CRC-valid copies made " +
                                        $"ordinal {(long)s.Header.Index + 1} for file {s.Header.FileId:x16} " +
                                        "a terminal erasure for this watch run.");
                                changed = true;
                                continue;
                            }
                            seen.Add(key, s);
                            shards.Add(s);
                            changed = true;
                        }
                        families = nextFamilies;
                        successful.ReleasePersistedConflicts(decoded);
                    }
                    if (changed)
                    {
                        PrintSetStatus(log, shards, services.Parity);
                        if (services.Parity.IsSetComplete(shards))
                        {
                            if (sessionPath is not null)
                                ValidateSessionPathAliases(sessionPath, current, outputPath);
                            var restored = services.Assembler.Assemble(shards, outputPath, log, password);
                            transaction?.Delete();
                            return ReportRestored(@out, restored, json);
                        }
                    }
                }
                // When a directory is large, never spend more than about half the watcher's time
                // rescanning unchanged names. Small normal folders retain the configured latency,
                // and Ctrl+C wakes the wait immediately even when WatchPollMs is large.
                int adaptivePollMs = Math.Max(pollMs,
                    (int)Math.Min(60_000, Math.Ceiling(scanElapsed.TotalMilliseconds)));
                cancellation.Token.WaitHandle.WaitOne(adaptivePollMs);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (sessionPath is not null && shards.Count > 0)
        {
            // New shards were appended as each batch arrived. An empty save still repairs a torn
            // final frame discovered at open, without walking/re-hashing the full retained set.
            transaction!.Save([]);
            log($"Stopped — {shards.Count} shard(s) saved to {ShardHeader.Display(sessionPath)}.");
        }
        if (json)
            @out.WriteLine(new JsonReports().DecodeIncompleteReport(shards, services.Parity,
                transaction?.ConflictedShardCount ?? noSessionTerminalKeys.Count));
        return 3;
    }

    /// <summary>
    /// Closing report of a decode that produced files. Every decode path ends here so the JSON
    /// shape cannot vary by how the shards were captured (folder, session, clipboard, watch,
    /// recording) — the paths are the part a script cannot otherwise learn, since without -o the
    /// destination is derived from the shard header and may take the ".restored" fallback.
    /// </summary>
    private static int ReportRestored(TextWriter @out, List<RestoredFile> restored, bool json)
    {
        @out.WriteLine(json
            ? new JsonReports().DecodeReport(restored)
            : $"Restored {restored.Count} file(s).");
        return 0;
    }

    /// <summary>
    /// Union of two shard lists. Byte-identical repeats are harmless; two differing CRC-valid
    /// copies make that ordinal a terminal erasure.  This is deliberately not "first wins": an
    /// attacker cannot poison a transfer by racing a counterfeit candidate into the session.
    /// </summary>
    private static List<DecodedShard> MergeShards(List<DecodedShard> first, List<DecodedShard> second,
        out int terminalConflicts)
    {
        terminalConflicts = 0;
        var seen = new Dictionary<(ulong, int, bool), DecodedShard?>();
        var families = new Dictionary<ulong, ShardHeader>();
        foreach (var s in first.Concat(second))
        {
            AddOrValidateFamily(families, s.Header);
            var key = (s.Header.FileId, s.Header.Index, s.Header.IsParity);
            if (seen.TryGetValue(key, out DecodedShard? existing))
            {
                if (existing is null || SameShard(existing, s))
                    continue;
                seen[key] = null;
                terminalConflicts++;
                continue;
            }
            seen.Add(key, s);
        }
        return seen.Values.Where(static shard => shard is not null)
            .Select(static shard => shard!).ToList();
    }

    private static Dictionary<ulong, ShardHeader> FamilyMap(IEnumerable<DecodedShard> shards)
    {
        var families = new Dictionary<ulong, ShardHeader>();
        foreach (DecodedShard shard in shards)
            AddOrValidateFamily(families, shard.Header);
        return families;
    }

    private static void AddOrValidateFamily(Dictionary<ulong, ShardHeader> families, ShardHeader header)
    {
        if (families.TryGetValue(header.FileId, out ShardHeader? family))
        {
            if (!family.HasSameFamilyAs(header))
                throw new ShardDecodeException(
                    $"Shard set contains inconsistent metadata for file {header.FileId:x16}; " +
                    "discard the conflicting captures and recapture them.");
        }
        else
        {
            families.Add(header.FileId, header);
        }
    }

    private static bool SameShard(DecodedShard left, DecodedShard right) =>
        left.Header.HasSameFamilyAs(right.Header) &&
        left.Header.PayloadLength == right.Header.PayloadLength &&
        left.Header.PayloadCrc32 == right.Header.PayloadCrc32 &&
        left.Payload.AsSpan().SequenceEqual(right.Payload);

    /// <summary>
    /// Refuses path aliases before any session state is opened. In particular, allowing
    /// <c>--session X -o X</c> would atomically publish the restored payload over X and then let
    /// successful-session cleanup delete that payload. Input aliases are equally unsafe because
    /// the final publish may replace the only capture used to reconstruct the file.
    /// </summary>
    private static void ValidateSessionPathAliases(string sessionPath,
        IEnumerable<string> inputPaths, string? outputPath)
    {
        string session = CanonicalPath(sessionPath);
        string sessionLease = CanonicalPath(sessionPath + ".lock");
        string? output = outputPath is null ? null : CanonicalPath(outputPath);
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (output is not null && PathsAlias(session, output, comparer))
            throw new ArgumentException("--session and -o/--out must refer to different paths.");
        if (output is not null && PathsAlias(sessionLease, output, comparer))
            throw new ArgumentException("-o/--out must not alias the reserved --session lease path.");

        foreach (string inputPath in inputPaths)
        {
            string input = CanonicalPath(inputPath);
            if (PathsAlias(session, input, comparer))
                throw new ArgumentException("--session must not alias an input capture path.");
            if (PathsAlias(sessionLease, input, comparer))
                throw new ArgumentException("An input capture must not alias the reserved --session lease path.");
            if (output is not null && PathsAlias(output, input, comparer))
                throw new ArgumentException("-o/--out must not alias an input capture path when --session is used.");
        }

        // Existing destinations are deliberately refused for session-backed restores. Besides
        // making retries recoverable, this closes alias namespaces the runtime cannot portably
        // resolve (for example a Unix bind mount): an alias of an existing session/capture also
        // exists at the output spelling and is stopped before either object can be replaced.
        if (output is not null && (File.Exists(output) || Directory.Exists(output)))
            throw new ArgumentException(
                "When --session is used, -o/--out must be a fresh path that does not already exist.");
    }

    private static bool PathsAlias(string left, string right, StringComparer comparer)
    {
        if (comparer.Equals(left, right))
            return true;
        if (!OperatingSystem.IsWindows())
            return false;
        return TryGetWindowsPathIdentity(left, out WindowsPathIdentity leftIdentity) &&
               TryGetWindowsPathIdentity(right, out WindowsPathIdentity rightIdentity) &&
               leftIdentity.File == rightIdentity.File && leftIdentity.File.FileIndex != 0 &&
               StringComparer.OrdinalIgnoreCase.Equals(leftIdentity.RemainingPath,
                   rightIdentity.RemainingPath);
    }

    private static bool TryGetWindowsPathIdentity(string path, out WindowsPathIdentity identity)
    {
        var remaining = new Stack<string>();
        string existing = path;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing)
            {
                identity = default;
                return false;
            }
            remaining.Push(Path.GetFileName(existing));
            existing = parent;
        }
        if (!TryGetWindowsFileIdentity(existing, out WindowsFileIdentity file))
        {
            identity = default;
            return false;
        }
        identity = new WindowsPathIdentity(file, string.Join('\\', remaining));
        return true;
    }

    private static bool TryGetWindowsFileIdentity(string path, out WindowsFileIdentity identity)
    {
        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFileW(path, 0, shareReadWriteDelete, 0,
            openExisting, backupSemantics, 0);
        if (!handle.IsInvalid && GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            identity = new WindowsFileIdentity(info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
            return true;
        }
        identity = default;
        return false;
    }

    internal static string CanonicalPath(string path)
    {
        if (OperatingSystem.IsWindows())
            return CanonicalWindowsPath(path);
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)
            ?? throw new ArgumentException("Path has no filesystem root.", nameof(path));
        string current = root;
        string relative = full[root.Length..];
        string[] segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string candidate = Path.Combine(current, segments[i]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                for (; i < segments.Length; i++)
                    current = Path.Combine(current, segments[i]);
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(candidate)
                    : new FileInfo(candidate);
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                    throw new IOException($"Could not resolve reparse-point path '{ShardHeader.Display(candidate)}'.");
                current = Path.GetFullPath(target.FullName);
            }
            else
            {
                current = candidate;
            }
        }

        current = Path.GetFullPath(current);
        if (!string.Equals(current, Path.GetPathRoot(current), StringComparison.Ordinal))
            current = Path.TrimEndingDirectorySeparator(current);
        return current.Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string CanonicalWindowsPath(string path)
    {
        string full = Path.GetFullPath(NormalizeWindowsDevicePath(path));
        var suffix = new Stack<string>();
        string existing = full;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null || parent == existing)
                throw new IOException($"Could not resolve path root for '{ShardHeader.Display(path)}'.");
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }

        const uint shareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFileW(existing, desiredAccess: 0, shareReadWriteDelete,
            securityAttributes: 0, openExisting, backupSemantics, templateFile: 0);
        if (handle.IsInvalid)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        uint needed = GetFinalPathNameByHandleW(handle, null, 0, 0);
        if (needed == 0 || needed > 32_768)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        var resolvedBuffer = new char[needed];
        uint written = GetFinalPathNameByHandleW(handle, resolvedBuffer, (uint)resolvedBuffer.Length, 0);
        if (written == 0 || written >= resolvedBuffer.Length)
            throw new IOException($"Could not resolve filesystem identity for '{ShardHeader.Display(existing)}'.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));

        string resolved = NormalizeLoopbackAdminShare(
            NormalizeWindowsDevicePath(new string(resolvedBuffer, 0, (int)written)));
        while (suffix.TryPop(out string? segment))
            resolved = Path.Combine(resolved, segment);
        resolved = Path.GetFullPath(resolved);
        if (!string.Equals(resolved, Path.GetPathRoot(resolved), StringComparison.OrdinalIgnoreCase))
            resolved = Path.TrimEndingDirectorySeparator(resolved);
        return resolved.Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string extended = @"\\?\";
        const string extendedUnc = @"\\?\UNC\";
        if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUnc.Length..];
        if (path.StartsWith(extended, StringComparison.OrdinalIgnoreCase))
        {
            string remainder = path[extended.Length..];
            if (remainder.Length >= 3 && char.IsAsciiLetter(remainder[0]) && remainder[1] == ':' &&
                (remainder[2] == '\\' || remainder[2] == '/'))
                return remainder;
            throw new ArgumentException(
                "Windows device/volume paths are not accepted for sessions, captures or output; use a drive or UNC path.");
        }
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Windows device paths are not accepted for sessions, captures or output; use a drive or UNC path.");
        return path;
    }

    private static string NormalizeLoopbackAdminShare(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        string[] parts = path[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[1].Length != 2 || parts[1][1] != '$' ||
            !char.IsAsciiLetter(parts[1][0]))
            return path;
        string server = parts[0];
        bool loopback = server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
                        server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (!loopback)
            return path;
        string local = parts[1][0] + @":\";
        return parts.Length == 2 ? local : Path.Combine([local, .. parts[2..]]);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess,
        uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[]? path,
        uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out ByHandleFileInformation information);

    private readonly record struct WindowsFileIdentity(uint VolumeSerialNumber, ulong FileIndex);
    private readonly record struct WindowsPathIdentity(WindowsFileIdentity File, string RemainingPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static void ReportSessionRecovery(ISessionTransaction transaction, TextWriter err)
    {
        if (transaction.RecoveryNotice is not null)
            err.WriteLine($"warning: {SafeMessage(transaction.RecoveryNotice)}");
    }

    private static void ReportTerminalConflicts(ISessionTransaction transaction, int previous,
        TextWriter err)
    {
        int added = transaction.ConflictedShardCount - previous;
        if (added > 0)
            err.WriteLine($"warning: {added:N0} shard ordinal(s) received conflicting CRC-valid " +
                "candidates and are now terminal erasures. They are treated as missing; later " +
                "copies cannot select a winner. Use recovery parity or start a fresh session.");
    }

    private static void PrintSetStatus(Action<string> write, List<DecodedShard> shards, IParityReassembler parity)
    {
        foreach (var group in shards.GroupBy(s => s.Header.FileId))
        {
            var first = group.First().Header;
            if (group.Any(s => !first.HasSameFamilyAs(s.Header)))
                throw new ShardDecodeException(
                    $"Shard set contains inconsistent metadata for file {first.FileId:x16}.");
            var have = group.Where(s => !s.Header.IsParity).Select(s => s.Header.Index).ToHashSet();
            int missingCount = first.Count - have.Count;
            var missing = new List<int>(Math.Min(20, missingCount));
            for (int i = 0; i < first.Count && missing.Count < 20; i++)
                if (!have.Contains(i))
                    missing.Add(i);
            int parityCount = group.Count(s => s.Header.IsParity);
            bool complete = parity.IsSetComplete([.. group]);
            string detail = missingCount == 0
                ? "all data present"
                : $"missing image(s) {string.Join(", ", missing.Select(i => (long)i + 1))}{(missingCount > missing.Count ? ", ..." : "")}";
            write($"  '{ShardHeader.Display(first.FileName)}': {have.Count}/{first.Count} data + {parityCount} parity — " +
                  $"{(complete ? "recoverable ✓" : detail)}");
        }
    }

    internal static (int Width, int Height) ParseResolution(string value)
    {
        int split = value.IndexOfAny(['x', 'X']);
        if (split < 0)
        {
            int r = int.Parse(value);
            return (r, r);
        }
        return (int.Parse(value[..split]), int.Parse(value[(split + 1)..]));
    }

    /// <summary>Fallback when "auto" is requested but no display can be detected (headless/remote).</summary>
    internal const int FallbackResolution = 2160;

    /// <summary>
    /// Resolves a resolution value: "auto" detects the primary monitor's native resolution
    /// (clamped into the encodable range), anything else parses as a number or WxH.
    /// The note is appended to the CLI's config line to say where an auto value came from.
    /// </summary>
    internal static (int Width, int Height, string Note) ResolveResolution(
        string value, Func<(int Width, int Height)?>? detect = null)
    {
        if (!value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var (w, h) = ParseResolution(value);
            return (w, h, "");
        }

        var detected = (detect ?? MonitorResolution.DetectPrimary)();
        if (detected is null)
            return (FallbackResolution, FallbackResolution, " (auto: no display detected, using fallback)");

        int width = Math.Clamp(detected.Value.Width, Layout.MinResolution, Layout.MaxResolution);
        int height = Math.Clamp(detected.Value.Height, Layout.MinResolution, Layout.MaxResolution);
        return (width, height, " (auto: primary monitor)");
    }

    /// <summary>
    /// Lazily expands already-validated CLI inputs. The caller immediately feeds this iterator to
    /// ShardDecoder.MaterializeInputPaths, which enforces the configured metadata allowance before
    /// sorting or retaining the whole directory listing.
    /// </summary>
    private static IEnumerable<string> EnumerateImageArguments(IEnumerable<string> arguments)
    {
        foreach (string path in arguments)
        {
            if (Directory.Exists(path))
            {
                foreach (string image in Directory.EnumerateFiles(path).Where(IsImageFile))
                    yield return image;
            }
            else
            {
                yield return path;
            }
        }
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".png" or ".apng" or ".gif" or ".bmp" or ".jpg" or ".jpeg" or ".webp" or ".tga" or ".qoi" or ".tif" or ".tiff";

    private static (List<string> Positional, Dictionary<string, string> Named, HashSet<string> Flags) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--no-compress" or "--camera" or "--video" or "--json" or "--watch" or "--screen" or "--open" or "--interleave2" or "--clipboard" or "--dry-run" or "--password-stdin")
                flags.Add(args[i]);
            else if (args[i].StartsWith('-') && i + 1 < args.Length)
                named[args[i]] = args[++i];
            else
                positional.Add(args[i]);
        }
        return (positional, named, flags);
    }

    private static string? Get(Dictionary<string, string> named, params string[] keys) =>
        keys.Select(k => named.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);

    private static int GetInt(Dictionary<string, string> named, string shortKey, string longKey, int fallback)
    {
        string? v = Get(named, shortKey, longKey);
        return v is null ? fallback : int.Parse(v);
    }

    private static double GetDouble(Dictionary<string, string> named, string key, double fallback)
    {
        string? v = Get(named, key);
        return v is null ? fallback : double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ResolvePassword(Dictionary<string, string> named, HashSet<string> flags,
        TextReader input)
    {
        string? direct = Get(named, "-p", "--password");
        string? passwordFile = Get(named, "--password-file");
        bool fromStdin = flags.Contains("--password-stdin");
        int sources = (direct is null ? 0 : 1) + (passwordFile is null ? 0 : 1) + (fromStdin ? 1 : 0);
        if (sources > 1)
            throw new ArgumentException(
                "Use exactly one password source: -p/--password, --password-file, or --password-stdin.");

        string? password = direct;
        if (passwordFile is not null)
        {
            string fullPath = Path.GetFullPath(passwordFile);
            using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            const int maxPasswordFileBytes = 64 * 1024;
            if (file.Length > maxPasswordFileBytes)
                throw new ArgumentException($"--password-file must be at most {maxPasswordFileBytes:N0} bytes.");
            using var reader = new StreamReader(file,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
            password = ReadBoundedPassword(reader, readOneLine: false);
            if (password.StartsWith('\ufeff')) // an optional UTF-8 BOM, never UTF-16/32 autodetection
                password = password[1..];
            if (password.EndsWith("\r\n", StringComparison.Ordinal))
                password = password[..^2];
            else if (password.EndsWith('\n') || password.EndsWith('\r'))
                password = password[..^1];
        }
        else if (fromStdin)
        {
            password = ReadBoundedPassword(input, readOneLine: true);
        }

        const int maxPasswordChars = 4_096;
        if ((passwordFile is not null || fromStdin) && password is { Length: > maxPasswordChars })
            throw new ArgumentException($"Password input must be at most {maxPasswordChars:N0} characters.");
        if (password is { Length: 0 })
            throw new ArgumentException("Passwords must not be empty. Omit password options for plaintext output.");
        return password;
    }

    private static string ReadBoundedPassword(TextReader reader, bool readOneLine)
    {
        const int maxPasswordChars = 4_096;
        // The logical limit applies after removing transport framing. Leave bounded room for an
        // optional decoded UTF-8 BOM plus one CRLF in a file, or the CR before stdin's LF. The
        // common check in ResolvePassword enforces the exact post-framing limit.
        int maxFramedChars = checked(maxPasswordChars + (readOneLine ? 1 : 3));
        var value = new System.Text.StringBuilder();
        while (true)
        {
            int next = reader.Read();
            if (next < 0 || (readOneLine && next == '\n'))
                break;
            if (value.Length >= maxFramedChars)
                throw new ArgumentException($"Password input must be at most {maxPasswordChars:N0} characters.");
            value.Append((char)next);
        }
        if (readOneLine && value.Length > 0 && value[^1] == '\r')
            value.Length--;
        return value.ToString();
    }

    /// <summary>Neutralizes terminal controls in a diagnostic while retaining enough text for
    /// actionable option and recovery guidance. User-facing path/name fields use the tighter
    /// ShardHeader.Display cap.</summary>
    private static string SafeMessage(string text)
        => ShardHeader.TerminalText(text, 2_048);

    private static double GetValidatedFps(Dictionary<string, string> named, double fallback,
        double max = double.MaxValue)
    {
        double fps = GetDouble(named, "--fps", fallback);
        if (!double.IsFinite(fps) || fps <= 0 || fps > max)
            throw new ArgumentException(max == double.MaxValue
                ? "--fps must be a finite number greater than 0."
                : $"--fps must be a finite number greater than 0 and at most {max}.");
        return fps;
    }

    /// <summary>Recognized options (take a value) and flags (boolean) per subcommand. The single
    /// source of truth for option validation; keep in sync with each handler's Get/flags calls.
    /// Internal so the shell completions in completions/ can be checked against it by a test —
    /// they are a hand-maintained copy of this table and would otherwise drift unnoticed.</summary>
    internal sealed record ArgSpec(string[] Options, string[] Flags);

    internal static readonly Dictionary<string, ArgSpec> ArgSpecs = new()
    {
        ["encode"] = new(
            ["-o", "--out", "-r", "--resolution", "-c", "--cell", "-b", "--bits", "-e", "--ecc",
             "-R", "--recovery", "-F", "--fountain", "-f", "--format", "-p", "--password",
             "--password-file", "-i", "--interval", "--slideshow", "--profile"],
            ["--json", "--camera", "--no-compress", "--interleave2", "--video", "--open", "--dry-run", "--password-stdin"]),
        // `test <file> [encode opts]` shares the encode density surface (BuildEncodeOptions reads
        // all of these), plus --camera. No --json: the test emits only a human verdict.
        ["test"] = new(
            ["-r", "--resolution", "-c", "--cell", "-b", "--bits", "-e", "--ecc", "-R", "--recovery",
             "-F", "--fountain", "-p", "--password", "--password-file", "-f", "--format", "--profile"],
            ["--camera", "--no-compress", "--interleave2", "--password-stdin"]),
        ["decode"] = new(["-o", "--out", "-p", "--password", "--password-file", "--session", "--fps"], ["--clipboard", "--watch", "--json", "--password-stdin"]),
        ["verify"] = new(["--session"], ["--json"]),
        ["info"] = new(["--heatmap", "--quality-heatmap"], ["--json"]),
        ["receive"] = new(["-o", "--out", "-p", "--password", "--password-file", "--region", "--device", "--format", "--fps"], ["--screen", "--password-stdin"]),
        ["calibrate"] = new(["-o", "--out", "-r", "--resolution"], ["--camera"]),
    };

    private static string? FindDuplicateOption(ReadOnlySpan<string> args, ArgSpec spec)
    {
        var valueOptions = new HashSet<string>(spec.Options, StringComparer.Ordinal);
        var flags = new HashSet<string>(spec.Flags, StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!valueOptions.Contains(token) && !flags.Contains(token))
                continue;

            string semantic = CanonicalOption(token);
            if (seen.TryGetValue(semantic, out string? prior))
                return prior == token
                    ? $"option '{token}' was supplied more than once; specify it exactly once."
                    : $"options '{prior}' and '{token}' are aliases; specify only one of them.";
            seen.Add(semantic, token);
            if (valueOptions.Contains(token) && i + 1 < args.Length)
                i++; // its value is opaque, even when it happens to start with '-'
        }
        return null;
    }

    private static string CanonicalOption(string option) => option switch
    {
        "--out" => "-o",
        "--resolution" => "-r",
        "--cell" => "-c",
        "--bits" => "-b",
        "--ecc" => "-e",
        "--recovery" => "-R",
        "--fountain" => "-F",
        "--format" => "-f",
        "--password" => "-p",
        "--interval" => "-i",
        _ => option,
    };

    /// <summary>Returns an actionable error if any option/flag is unrecognized for the command, else null.</summary>
    private static string? ValidateOptions(ArgSpec spec, Dictionary<string, string> named, HashSet<string> flags, List<string> positional)
    {
        var known = new HashSet<string>(spec.Options, StringComparer.Ordinal);
        foreach (var f in spec.Flags)
            known.Add(f);

        // Named options and flags that are not part of this command's surface (catches --pasword,
        // and a valid-but-wrong-command flag like --camera on decode).
        foreach (string key in named.Keys)
            if (!known.Contains(key))
                return UnknownOption(key, known);
        foreach (string flag in flags)
            if (!known.Contains(flag))
                return UnknownOption(flag, known);

        // A '-'-prefixed positional is a misspelled flag that fell through ParseArgs (e.g. a typo'd
        // trailing flag). Genuine negative-number values are never positional here.
        foreach (string p in positional)
            if (p.Length > 1 && p[0] == '-' && !(p.Length > 1 && (char.IsDigit(p[1]) || p[1] == '.')))
                return UnknownOption(p, known);

        // A value that is itself a known option means the option before it lost its value (e.g.
        // `--recovery --camera` silently drops --recovery).
        //
        // -p/--password used to be exempt on the grounds that a password may legitimately start
        // with '-'. True, but far broader than the justification needs: this fires only when the
        // value is EXACTLY one of this command's own options, which no real password is. The
        // exemption's cost is severe and silent — `qrshard encode secrets.db -p --json` encrypted
        // with the literal password "--json", exit 0, while the user believed they had used
        // theirs. They cannot ever decrypt it, and nothing in the output says why.
        foreach (var (key, val) in named)
            if (val.Length > 1 && val[0] == '-' && known.Contains(val))
                return key is "-p" or "--password"
                    ? $"option '{key}' looks like it lost its value: '{val}' is another option, so the " +
                      $"payload would be encrypted with '{val}' as the password. Passwords may start " +
                      "with '-', but cannot be exactly one of this command's option names."
                    : $"option '{key}' is missing a value ('{val}' is another option).";

        return null;
    }

    private static string UnknownOption(string got, HashSet<string> known)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (string k in known)
        {
            int d = Levenshtein(got, k);
            if (d < bestDist)
            {
                bestDist = d;
                best = k;
            }
        }
        string hint = best is not null && bestDist <= 3 ? $" Did you mean '{best}'?" : "";
        return $"unknown option '{got}'.{hint}";
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = a[i - 1] == b[j - 1] ? prev : 1 + Math.Min(prev, Math.Min(d[j], d[j - 1]));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    /// <summary>Maps the shared density options (-c/-b/-e/-f/-p/--camera/--interleave2/--no-compress)
    /// from parsed args, honoring the camera-profile defaults. The encode path layers IsArchive/
    /// recovery on top; `test` reuses it directly so the two can't diverge.</summary>
    /// <summary>Resolves the encode defaults, applying a named --profile if given. Returns null and
    /// sets <paramref name="error"/> when the profile name is unknown. Shared by encode and test.</summary>
    private static AppSettings.EncodeDefaultSettings? ResolveProfile(Dictionary<string, string> named, AppSettings settings, out string? error)
    {
        error = null;
        var defaults = settings.EncodeDefaults;
        string? profileName = Get(named, "--profile");
        if (profileName is not null)
        {
            if (!settings.EncodeProfiles.TryGetValue(profileName, out var profile))
            {
                error = $"unknown profile '{profileName}'. Defined: " +
                    (settings.EncodeProfiles.Count == 0 ? "(none)" : string.Join(", ", settings.EncodeProfiles.Keys));
                return null;
            }
            defaults = profile;
        }
        return defaults;
    }

    private static EncodeOptions BuildEncodeOptions(Dictionary<string, string> named, HashSet<string> flags,
        AppSettings.EncodeDefaultSettings defaults, bool camera, int width, int height,
        string? password) => new()
    {
        Width = width,
        Height = height,
        CellPx = GetInt(named, "-c", "--cell", camera ? 8 : defaults.CellPx),
        BitsPerCell = GetInt(named, "-b", "--bits", camera ? 2 : defaults.BitsPerCell),
        EccParity = GetInt(named, "-e", "--ecc", camera ? 32 : defaults.EccParity),
        RecoveryPercent = GetInt(named, "-R", "--recovery", defaults.RecoveryPercent),
        FountainPercent = GetInt(named, "-F", "--fountain", 0),
        ImageFormat = Get(named, "-f", "--format") ?? defaults.ImageFormat,
        Compress = !flags.Contains("--no-compress") && defaults.Compress,
        CameraMode = camera,
        Password = password,
        Interleave2 = flags.Contains("--interleave2"),
    };

    private const int DryRunImageWarnThreshold = 500;

    /// <summary>Prints the encode plan (image counts, geometry) without rendering anything.</summary>
    private static int DryRun(EncodePlan plan, string outDir, bool json, TextWriter @out)
    {
        if (json)
        {
            @out.WriteLine(new JsonReports().DryRunReport(plan, outDir));
            return 0;
        }
        @out.WriteLine($"Dry run — no images written. This encode would produce, in {ShardHeader.Display(outDir)}:");
        string split = plan.ParityImages > 0
            ? $"{plan.DataImages} data + {plan.ParityImages} recovery image(s)"
            : $"{plan.DataImages} data image(s)";
        @out.WriteLine($"  {plan.ImageCount} image(s) of {plan.Width}x{plan.Height}px ({split}), up to {plan.BytesPerImage:N0} payload bytes each, format {plan.Format}.");
        if (plan.ImageCount > DryRunImageWarnThreshold)
            @out.WriteLine($"  note: {plan.ImageCount} images is a lot to capture — raise density with a larger --resolution, smaller --cell, or more --bits, or lower --recovery.");
        return 0;
    }

    /// <summary>
    /// Prints the tool version. Read from the assembly's informational version, which is what the
    /// csproj &lt;Version&gt; flows into — so it cannot drift from the released package the way a
    /// hand-maintained constant would. Deterministic builds append "+&lt;commit&gt;" to that
    /// attribute; that is noise here, so only the part before '+' is printed.
    /// </summary>
    private static int Version(TextWriter @out)
    {
        var assembly = typeof(Cli).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string version = !string.IsNullOrWhiteSpace(informational)
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? "unknown";
        @out.WriteLine($"qrshard {version}");
        return 0;
    }

    private static int Help(TextWriter @out, TextWriter err, string? error = null)
    {
        if (error is not null)
            err.WriteLine($"error: {SafeMessage(error)}\n");
        @out.WriteLine(
            """
            QrShard — encode any file into dense QR-style images and back.

            usage:
              qrshard encode <file|folder>... [options]
                                         Split one file directly, or tar one or more files/folders
                                         into an archive that is extracted on decode.
                -o, --out <dir>          Output folder (default: <name>.shards next to the first
                                         input; multiple inputs use bundle.shards)
                -r, --resolution <px>    Image size: "auto" (the default) uses the primary
                                         monitor's native resolution so shards fill the screen
                                         they'll be captured from; or one number (square) or
                                         WxH, 700-16384, to override (e.g. a smaller size shows
                                         the code surrounded by padding)
                -c, --cell <px>          Data cell size in pixels, 1-64 (default: 3)
                -b, --bits <n>           Bits per cell / color density, 1-8 (default: 4)
                -e, --ecc <n>            Reed-Solomon parity per 255-byte block, even, 0-64
                                         (default: 16 ≈ 6% overhead, fixes 8 bad bytes per block)
                -R, --recovery <pct>     Add parity IMAGES so whole missing/damaged images can be
                                         rebuilt without recapture; pct% of DATA images, allocated
                                         per stripe, 0-100 (R15 is about 15 parity per 100 data,
                                         or 13% of the resulting set; tolerance is per stripe)
                -F, --fountain <pct>     Fountain coding for video mode: 0-1000% extra CODED frames
                                         (random linear combinations); a full-rank set of roughly
                                         stripeData captured frames per stripe reconstructs it —
                                         duplicate/dependent/torn/glared frames don't count;
                                         mutually exclusive with -R
                -p, --password <pw>      AES-256-GCM encrypt the payload; decode needs the same
                                         password. Failure publishes no plaintext, but shard
                                         metadata stays visible. WARNING: argv may be exposed in
                                         shell history and process listings
                --password-file <file>  Read the password from a bounded UTF-8 file (one final
                                         line ending is removed); avoids argv exposure
                --password-stdin        Read one bounded password line from standard input;
                                         mutually exclusive with the other password sources
                -f, --format <fmt>       Lossless image format: png, bmp, tga, qoi, webp, tiff
                                         (default: png, written by the built-in fast PNG writer)
                --camera                 Camera profile: adds finder patterns so images decode
                                         from PHOTOS of the screen (rotation + perspective), not
                                         just screenshots; shifts defaults to cell 8, 2 bits,
                                         ECC 32 (explicit flags still win). Far lower density —
                                         use for small/medium payloads
                --video                  Also write slideshow.html: a relative manifest cycling
                                         the adjacent shard/sidecar files forever. Keep the page
                                         beside those files; record at least one full cycle
                -i, --interval <ms>      Slideshow interval per image (default: 500, min 100)
                --slideshow <kind>       With --video: "html" (default) or "apng" (a single
                                         animated PNG cycling the shards). APNG refuses more than
                                         256 MiB of decoded RGB frames; HTML scales further
                --open                   With --video, open the slideshow after encoding
                                         (cannot be combined with --json)
                --interleave2            v2 permuted interleave: spreads VERTICAL damage as well
                                         as horizontal (needs ECC; older decoders reject it)
                --profile <name>         Apply a named encode preset from appsettings.json
                                         (flags still override it)
                --json                   Emit the encode result as JSON on stdout
                --dry-run                Print the image count and geometry without writing any
                                         images (preview before a folder emits hundreds of PNGs)
                --no-compress            Skip compression of the payload
                Multiple inputs (files and/or folders) are bundled into one archive and
                extracted on decode: qrshard encode a.bin b.bin docs/ -o out.shards.
                Top-level links are refused and folder reparse links skipped; hard links are
                copied as regular files; non-portable path names/aliases are refused. Archives
                are limited to 100,000 entries and 128 path segments per entry; decode caps its
                path index at 200,000 nodes. Single-file/prepared-archive payloads are capped at
                1.5 GB.
              qrshard send <file|folder>... [encode options]
                                         One-step sender: encode with a slideshow and open it
                                         in the default browser

              qrshard decode <folder|images...|recording> [-o <path>]
                                         Reconstitute the original file from captured images, or
                                         from a screen/phone RECORDING of the slideshow
                                         (mp4/webm/mkv/mov/avi need ffmpeg on PATH; animated
                                         png/gif/webp decode natively)
                -o, --out <path>         Output file, or directory for an archive. A single file is
                                         staged and verified before atomic publication. An archive
                                         is staged and published complete; its destination must be
                                         absent or empty and is never merged
                --fps <n>                Finite frame extraction rate > 0 for video files
                                         (default: 8)
                -p, --password <pw>      Password for encrypted payloads. WARNING: argv may be
                                         exposed in shell history and process listings
                --password-file <file>  Read the password from a bounded UTF-8 file
                --password-stdin        Read one bounded password line from standard input
                --session <file>         Accumulate shards across capture sittings: incomplete
                                         sets persist to the session file (exit code 3) and the
                                         next run resumes from the union; a complete retained
                                         session can be retried with 'decode --session <file>';
                                         deleted only after successful output publication.
                                         Images only — a recording is re-read from the start
                --watch                  Keep watching the folder: decode captures as they land
                                         and assemble the moment the set completes (Ctrl+C
                                         stops; progress persists when --session is given)
                --clipboard              (Windows) decode the bitmap on the clipboard —
                                         Win+Shift+S a displayed shard, no file saving;
                                         accumulates with --session
                --json                   Emit the result as JSON on stdout: the restored file(s)
                                         with their resolved paths and lengths, or — when the set
                                         is incomplete (exit 3) — the same per-file status verify
                                         reports
              qrshard receive [--device d] [--format fmt] [--screen] [--region x,y,w,h]
                              [--fps n] [-o f] [-p pw]
                                         LIVE receiver: decode a webcam pointed at the sender's
                                         slideshow — or, with --screen, THIS machine's own
                                         screen (put the slideshow in an RDP/VM window and
                                         transfer out of locked-down remotes). Stops
                                         automatically when the transfer completes.
                --device <d>             Camera/capture device. Windows requires a name (list with
                                         ffmpeg -list_devices true -f dshow -i dummy); defaults:
                                         Linux /dev/video0, macOS 0
                --format <fmt>           ffmpeg input format (defaults: Windows dshow, Linux v4l2,
                                         macOS avfoundation)
                --screen                 Capture this machine's display instead of a camera
                --region <x,y,w,h>       With --screen: x/y are integers; w/h must be positive
                --fps <n>                Finite sampling rate > 0 and <= 120 (default: 10 or
                                         appsettings ReceiveFps)
                -o, --out <path>         Output file or archive directory
                -p, --password <pw>      Password for encrypted payloads (argv warning above)
                --password-file <file>  Read the password from a bounded UTF-8 file
                --password-stdin        Read one bounded password line from standard input
              qrshard calibrate [-o dir] [-r res] [--camera]
                                         Write a ladder of density probes (--camera for the
                                         photo-capture ladder); capture them like a real
                                         transfer, then run qrshard calibrate <folder> to get
                                         recommended -c/-b settings for YOUR setup
              qrshard verify <folder|images...> [--session f] [--json]
                                         Report per-file completeness (missing images, parity
                                         coverage) without writing output; exit 0 when complete,
                                         3 when images are still missing
                                         (--json for machine-readable output, also on info)
              qrshard info <image> [--heatmap <out.png>] [--quality-heatmap <out.png>] [--json]
                                         Show and validate a single shard image. --heatmap renders
                                         a per-cell ECC damage map (green=clean, red=corrected,
                                         dark red=beyond correction), falling back to the quality
                                         map when the decode didn't complete. --quality-heatmap
                                         always renders the capture-QUALITY map (per-cell
                                         classification confidence) — works even when a capture
                                         fails to decode, so you can see WHERE glare/blur hit
                --json                   Emit shard information as JSON on stdout
              qrshard test [<file> [encode opts]]
                                         With no file: built-in round-trip self-test. With a file:
                                         encode YOUR file at YOUR settings (-c/-b/-e/--camera/...),
                                         run it through simulated screenshots, and report whether
                                         it survives and how much ECC headroom it used.

              qrshard --version          Print the version (also -v, or "version").
              qrshard --help             Show this help (also -h, or "help").

            Exit codes: 0 success; 2 usage error; 3 valid but incomplete — capture the missing
            images and run again; 1 anything else (unusable images, corruption, I/O).

            Density guide (per image, after default ECC): bytes ≈ cells x bits/cell / 8 x 0.94.
              Robust default (2160px, cell 3, 4 bits) ≈ 212 KB/image.
              Pixel-perfect captures can push cell 1-2 and 6-8 bits for multi-MB images.
            Capture tips: screenshot the image displayed at 100% zoom; include the full black
            frame with some white margin; avoid fractional display scaling for cell sizes < 3.
            ECC absorbs localized damage (cursor, notification toast, mild JPEG artifacts).
            """);
        return error is null ? 0 : 2;
    }
}
