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
    public int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var @out = stdout ?? Console.Out;
        var err = stderr ?? Console.Error;
        var cfg = settings ?? AppSettings.Current;
        try
        {
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
            return RunCore(args, @out, err, cfg, services);
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
                or InvalidOperationException or IOException or UnauthorizedAccessException
                or FormatException or OverflowException;
            if (inners.Count > 0 && inners.All(Handled))
            {
                err.WriteLine($"error: {inners[0].Message}");
                return 1;
            }
            throw;
        }
    }

    private static int RunCore(string[] args, TextWriter @out, TextWriter err, AppSettings settings, CliServices services)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help(@out, err);

        string command = args[0].ToLowerInvariant();
        // Reject unknown/misspelled options up front. Without this, ParseArgs accepts any
        // `-x value` pair blindly, so a typo like `--pasword pw` silently encodes UNENCRYPTED and
        // `--recvery 30` silently yields zero parity — data-exposure/data-loss for an integrity
        // tool. Validated against a per-command allowlist so it can't drift from the handlers.
        if (ArgSpecs.TryGetValue(command, out var spec))
        {
            var (pos, nm, fl) = ParseArgs(args[1..]);
            if (ValidateOptions(spec, nm, fl, pos) is { } optionError)
                return Help(@out, err, optionError);
        }

        switch (command)
        {
            case "encode":
            {
                var (positional, named, flags) = ParseArgs(args[1..]);
                if (positional.Count == 0)
                    return Help(@out, err, "encode requires one or more input files or folders.");
                foreach (string p in positional)
                    if (!File.Exists(p) && !Directory.Exists(p))
                        return Help(@out, err, $"not found: {p}");
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
                string? tempTarDir = positional.Count > 1 || Directory.Exists(positional[0])
                    ? Path.Combine(Path.GetTempPath(), "qrshard-tar-" + Guid.NewGuid().ToString("N")[..8])
                    : null;
                try
                {
                if (isArchive)
                {
                    Directory.CreateDirectory(tempTarDir!);
                    file = Path.Combine(tempTarDir!, inputName + ".tar");
                    preLog(positional.Count > 1
                        ? $"Archiving {positional.Count} inputs..."
                        : $"Archiving folder '{input}'...");
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
                var opt = BuildEncodeOptions(named, flags, defaults, camera, width, height) with { IsArchive = isArchive };
                string outDir = Get(named, "-o", "--out") ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(Path.TrimEndingDirectorySeparator(input)))!,
                    inputName + settings.ShardFolderSuffix);

                if (flags.Contains("--dry-run"))
                    return DryRun(services.Encoder.Plan(file, opt), outDir, json, @out);

                encLog($"Encoding '{input}' → {outDir}");
                encLog($"  {opt.Width}x{opt.Height}px{resolutionNote}, cell {opt.CellPx}px, {opt.BitsPerCell} bits/cell, " +
                       $"ECC parity {opt.EccParity}, recovery {opt.RecoveryPercent}%, " +
                       $"format {opt.ImageFormat}, compression {(opt.Compress ? "on" : "off")}" +
                       (camera ? ", camera profile (finder patterns)" : ""));
                var result = services.Encoder.Encode(file, outDir, opt, encLog);

                string? slideshowPath = null;
                if (flags.Contains("--video"))
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
                    @out.WriteLine($"Slideshow: {slideshowPath} ({intervalMs} ms/image, ~{result.ImageCount * intervalMs / 1000.0:0.#} s per cycle).");
                    @out.WriteLine(slideshowPath.EndsWith(".apng", StringComparison.OrdinalIgnoreCase)
                        ? "  Open it, view fullscreen, and record the screen for at least one full cycle."
                        : "  Open it in a browser, press F11, and record the screen for at least one full cycle.");
                    if (flags.Contains("--open"))
                        OpenInBrowser(slideshowPath, @out);
                }
                return 0;
                }
                finally
                {
                    if (tempTarDir is not null && Directory.Exists(tempTarDir))
                        Directory.Delete(tempTarDir, recursive: true);
                }
            }

            case "decode":
            {
                var (positional, named, dflags) = ParseArgs(args[1..]);
                string? password = Get(named, "-p", "--password");
                if (dflags.Contains("--clipboard"))
                    return DecodeClipboard(services, Get(named, "--session"), Get(named, "-o", "--out"), password, @out, err);
                if (positional.Count == 0)
                    return Help(@out, err, "decode requires a folder, image files, a video recording, or --clipboard.");

                if (dflags.Contains("--watch"))
                {
                    if (positional.Count != 1 || !Directory.Exists(positional[0]))
                        return Help(@out, err, "--watch requires exactly one folder to watch.");
                    return DecodeWatch(services, positional[0], Get(named, "--session"),
                        Get(named, "-o", "--out"), password, @out, settings.WatchPollMs);
                }

                // A single video file (or animated image) is a recording of the slideshow.
                if (positional.Count == 1 && File.Exists(positional[0]) &&
                    (VideoDecoder.IsVideoFile(positional[0]) ||
                     (IsImageFile(positional[0]) && VideoDecoder.IsAnimatedImage(positional[0]))))
                {
                    double fps = GetDouble(named, "--fps", 8.0);
                    @out.WriteLine($"Decoding video '{positional[0]}' (extracting at {fps} fps)...");
                    // Escalate fps automatically for file recordings unless the user pinned --fps.
                    bool userSetFps = Get(named, "--fps") is not null;
                    var fromVideo = services.VideoDecoder.Decode(positional[0], Get(named, "-o", "--out"), fps,
                        @out.WriteLine, out _, password, decodeWorkers: 1, escalateFps: !userSetFps);
                    @out.WriteLine($"Restored {fromVideo.Count} file(s).");
                    return 0;
                }

                var images = new List<string>();
                foreach (string p in positional)
                {
                    if (Directory.Exists(p))
                        images.AddRange(Directory.EnumerateFiles(p).Where(IsImageFile));
                    else if (File.Exists(p))
                        images.Add(p);
                    else
                        return Help(@out, err, $"not found: {p}");
                }
                if (images.Count == 0)
                    return Help(@out, err, "no image files found to decode.");

                string? sessionPath = Get(named, "--session");
                if (sessionPath is not null)
                    return DecodeWithSession(services, images, sessionPath, Get(named, "-o", "--out"), password, @out);

                @out.WriteLine($"Decoding {images.Count} image(s)...");
                var shards = services.Decoder.CollectShards(images, @out.WriteLine);
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
                    var restored = services.Assembler.Assemble(shards, Get(named, "-o", "--out"), @out.WriteLine, password);
                    @out.WriteLine($"Restored {restored.Count} file(s).");
                    return 0;
                }
                catch (ShardDecodeException ex)
                {
                    // Distinguish "whole images missing/unreadable" (recoverable by capturing more)
                    // from a complete-but-corrupt set (genuine data corruption). Only the former is
                    // nudged toward the resumable flow with the documented incomplete exit code.
                    if (services.Parity.IsSetComplete(shards))
                    {
                        err.WriteLine($"error: {ex.Message}");
                        return 1;
                    }
                    PrintSetStatus(@out, shards, services.Parity);
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
                var images = new List<string>();
                foreach (string p in positional)
                {
                    if (Directory.Exists(p))
                        images.AddRange(Directory.EnumerateFiles(p).Where(IsImageFile));
                    else if (File.Exists(p))
                        images.Add(p);
                    else
                        return Help(@out, err, $"not found: {p}");
                }
                string? session = Get(named, "--session");
                if (images.Count == 0 && session is null)
                    return Help(@out, err, "verify requires a folder, image files, or --session.");

                var shards = images.Count > 0
                    ? services.Decoder.CollectShards(images, json ? _ => { } : @out.WriteLine)
                    : [];
                if (session is not null)
                    shards = MergeShards(services.Sessions.Load(session), shards);
                if (shards.Count == 0)
                {
                    err.WriteLine("error: no decodable shards found.");
                    return 1;
                }

                bool complete = services.Parity.IsSetComplete(shards);
                if (json)
                {
                    @out.WriteLine(new JsonReports().VerifyReport(shards, services.Parity));
                    return complete ? 0 : 1;
                }
                PrintSetStatus(@out, shards, services.Parity);
                @out.WriteLine(complete
                    ? "Complete: every file can be fully reassembled."
                    : "Incomplete: capture the missing images and verify again.");
                return complete ? 0 : 1;
            }

            case "info":
            {
                var (positional, named, iflags) = ParseArgs(args[1..]);
                if (positional.Count != 1 || !File.Exists(positional[0]))
                    return Help(@out, err, "info requires one shard image.");
                bool json = iflags.Contains("--json");
                DecodedShard shard;
                string? heatmapPath = Get(named, "--heatmap");
                string? renderedHeatmap = null;
                int correctedCw = 0, failedCw = 0;
                if (heatmapPath is not null)
                {
                    var diag = services.Decoder.Diagnose(positional[0]);
                    if (diag.Layout is not null && diag.Layout.EccParity > 0)
                    {
                        services.Heatmap.Render(diag.Layout, diag.CodewordErrors, heatmapPath);
                        renderedHeatmap = heatmapPath;
                        correctedCw = diag.CodewordErrors.Count(e => e > 0);
                        failedCw = diag.CodewordErrors.Count(e => e < 0);
                        if (!json)
                            @out.WriteLine($"heatmap   : {heatmapPath} ({correctedCw} codeword(s) needed correction, {failedCw} beyond correction)");
                    }
                    else
                    {
                        err.WriteLine(diag.Layout is null
                            ? $"error: cannot render heatmap: {diag.Error}"
                            : "error: cannot render heatmap: this image was encoded without ECC.");
                    }
                    if (diag.Shard is null)
                    {
                        err.WriteLine($"error: {diag.Error}");
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
                @out.WriteLine($"file      : {h.FileName}");
                @out.WriteLine($"file id   : {h.FileId:X16}");
                @out.WriteLine($"part      : {(h.IsParity ? $"parity #{h.Index + 1}" : $"{h.Index + 1} of {h.Count}")}");
                @out.WriteLine($"payload   : {h.PayloadLength:N0} bytes (CRC-32 verified)");
                if (h.StripeParity > 0)
                    @out.WriteLine($"recovery  : {h.StripeParity} parity per {h.StripeData} data images per stripe");
                @out.WriteLine($"ecc       : {(shard.EccParity > 0 ? $"RS parity {shard.EccParity}, corrected {shard.CorrectedBytes} byte(s)" : "none")}");
                @out.WriteLine($"original  : {h.OriginalLength:N0} bytes{((h.Flags & ShardHeader.FlagCompressed) != 0 ? $", deflate-compressed to {h.TotalLength:N0}" : "")}");
                @out.WriteLine($"sha-256   : {Convert.ToHexStringLower(h.Sha256)}");
                return 0;
            }

            case "send":
                // One-step sender: encode with a slideshow and open it in the default browser.
                return RunCore(["encode", .. args[1..], "--video", "--open"], @out, err, settings, services);

            case "receive":
            {
                var (_, named, rflags) = ParseArgs(args[1..]);
                IFrameSource source;
                string sourceLabel;
                if (rflags.Contains("--screen"))
                {
                    // Self-capture: decode this machine's own screen — put the sender's
                    // slideshow anywhere visible, including inside an RDP/VM window.
                    source = new ScreenFrameSource(ScreenFrameSource.ParseRegion(Get(named, "--region")));
                    sourceLabel = "screen";
                    @out.WriteLine("Receiving from this machine's screen — put the sender's slideshow somewhere visible (an RDP or VM window works).");
                }
                else
                {
                    string? device = Get(named, "--device") ?? LiveFrameSource.DefaultDevice();
                    if (device is null)
                        return Help(@out, err,
                            "receive on Windows needs --device \"<webcam name>\" or --screen (list devices with: ffmpeg -list_devices true -f dshow -i dummy)");
                    source = new LiveFrameSource(Get(named, "--format"));
                    sourceLabel = device;
                    @out.WriteLine($"Receiving from '{device}' — point the camera at the sender's slideshow.");
                }
                double fps = GetDouble(named, "--fps", settings.ReceiveFps);
                int workers = settings.ReceiveDecodeWorkers > 0
                    ? settings.ReceiveDecodeWorkers
                    : Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
                @out.WriteLine($"Decoding at {fps} fps with {workers} worker(s); stops automatically when the transfer completes.");

                var live = new VideoDecoder(services.Decoder, source,
                    services.Assembler, services.Parity, new CameraRectifier());
                var received = live.Decode(sourceLabel, Get(named, "-o", "--out"), fps, @out.WriteLine, out var liveStats,
                    Get(named, "-p", "--password"), workers);
                @out.WriteLine($"Restored {received.Count} file(s) after examining {liveStats.FramesExamined} frame(s).");
                return 0;
            }

            case "calibrate":
            {
                var (positional, named, cflags) = ParseArgs(args[1..]);
                if (positional.Count == 1 && Directory.Exists(positional[0]))
                    return services.Calibration.Analyze(positional[0], @out);
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
                    return services.SelfTest.Run() ? 0 : 1; // built-in fixed-fixture self-test
                if (positional.Count != 1 || !File.Exists(positional[0]))
                    return Help(@out, err, "test takes no arguments (built-in self-test) or one file to round-trip at your settings.");
                var tDefaults = ResolveProfile(named, settings, out string? tProfileError);
                if (tDefaults is null)
                    return Help(@out, err, tProfileError);
                bool camera = tflags.Contains("--camera");
                var (width, height, _) = ResolveResolution(Get(named, "-r", "--resolution") ?? tDefaults.Resolution);
                var opt = BuildEncodeOptions(named, tflags, tDefaults, camera, width, height);
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
        string? outputPath, string? password, TextWriter @out)
    {
        var known = services.Sessions.Load(sessionPath);
        if (known.Count > 0)
            @out.WriteLine($"  session: resuming with {known.Count} previously collected shard(s)");

        @out.WriteLine($"Decoding {images.Count} image(s)...");
        var merged = MergeShards(known, services.Decoder.CollectShards(images, @out.WriteLine));
        if (merged.Count == 0)
            throw new ShardDecodeException("No decodable shard images were found.");

        if (services.Parity.IsSetComplete(merged))
        {
            var restored = services.Assembler.Assemble(merged, outputPath, @out.WriteLine, password);
            @out.WriteLine($"Restored {restored.Count} file(s).");
            if (File.Exists(sessionPath))
                File.Delete(sessionPath);
            return 0;
        }

        services.Sessions.Save(sessionPath, merged);
        PrintSetStatus(@out, merged, services.Parity);
        @out.WriteLine($"Set incomplete — {merged.Count} shard(s) saved to {sessionPath}; capture the missing images and decode again with --session.");
        return 3;
    }

    /// <summary>
    /// Clipboard decode (Windows): read the bitmap off the clipboard — Win+Shift+S a displayed
    /// shard, no file saving — merge it with the session, assemble when complete.
    /// </summary>
    private static int DecodeClipboard(CliServices services, string? sessionPath, string? outputPath,
        string? password, TextWriter @out, TextWriter err)
    {
        if (!OperatingSystem.IsWindows())
        {
            err.WriteLine("error: --clipboard is only supported on Windows.");
            return 1;
        }
        var bmp = new ClipboardReader().TryRead();
        if (bmp is null)
        {
            err.WriteLine("error: no bitmap on the clipboard (screenshot a displayed shard first).");
            return 1;
        }

        var shard = services.Decoder.DecodeBitmap(bmp, new DecodeScratch(), "clipboard");
        string which = shard.Header.IsParity
            ? $"parity #{shard.Header.Index + 1}"
            : $"part {shard.Header.Index + 1}/{shard.Header.Count}";
        @out.WriteLine($"  ok      clipboard  ({which}, {shard.Payload.Length:N0} bytes)");

        var known = sessionPath is not null ? services.Sessions.Load(sessionPath) : [];
        var merged = MergeShards(known, [shard]);
        if (services.Parity.IsSetComplete(merged))
        {
            var restored = services.Assembler.Assemble(merged, outputPath, @out.WriteLine, password);
            @out.WriteLine($"Restored {restored.Count} file(s).");
            if (sessionPath is not null && File.Exists(sessionPath))
                File.Delete(sessionPath);
            return 0;
        }
        if (sessionPath is null)
        {
            @out.WriteLine("Set incomplete — use --session <file> to accumulate clipboard captures across screenshots.");
            return 3;
        }
        services.Sessions.Save(sessionPath, merged);
        PrintSetStatus(@out, merged, services.Parity);
        @out.WriteLine($"Set incomplete — {merged.Count} shard(s) saved to {sessionPath}; screenshot the next image and run again.");
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
    private static void WriteTar(IReadOnlyList<string> inputs, string tarPath)
    {
        bool prefixFolders = inputs.Count > 1;
        var entries = new List<(string Source, string Name)>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                string prefix = prefixFolders ? Path.GetFileName(root) + "/" : "";
                foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/');
                    entries.Add((f, prefix + rel));
                }
            }
            else
            {
                entries.Add((input, Path.GetFileName(input)));
            }
        }

        var collision = entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (collision is not null)
            throw new ArgumentException(
                $"Two inputs map to the same archive path '{collision.Key}'; rename one or place them in separate folders " +
                "(a folder input keeps its subtree, so files with the same name in different subfolders are fine).");

        using var fs = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new System.Formats.Tar.TarWriter(fs, System.Formats.Tar.TarEntryFormat.Pax);
        foreach (var (source, name) in entries)
            writer.WriteEntry(source, name);
    }

    /// <summary>Opens the slideshow in the platform's default browser (suppressed by the
    /// QRSHARD_NO_LAUNCH environment variable, e.g. in tests and scripts).</summary>
    private static void OpenInBrowser(string path, TextWriter @out)
    {
        if (Environment.GetEnvironmentVariable("QRSHARD_NO_LAUNCH") is not null)
        {
            @out.WriteLine("  (browser launch suppressed by QRSHARD_NO_LAUNCH)");
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", path);
            else
                System.Diagnostics.Process.Start("xdg-open", path);
            @out.WriteLine("  Opened the slideshow in your default browser — press F11 for fullscreen.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            @out.WriteLine($"  Could not launch a browser automatically; open {path} manually.");
        }
    }

    /// <summary>
    /// Watch mode: poll a folder for new captures, decode each as it lands (with a settle
    /// delay so half-written screenshots are left for the next poll), and assemble the moment
    /// the set completes. Ctrl+C stops the watch, persisting progress when a session is given.
    /// </summary>
    private static int DecodeWatch(CliServices services, string folder, string? sessionPath,
        string? outputPath, string? password, TextWriter @out, int pollMs = 250)
    {
        var shards = sessionPath is not null ? services.Sessions.Load(sessionPath) : [];
        if (shards.Count > 0)
            @out.WriteLine($"  session: resuming with {shards.Count} previously collected shard(s)");
        var seen = shards.Select(s => (s.Header.FileId, s.Header.Index, s.Header.IsParity)).ToHashSet();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        @out.WriteLine($"Watching {folder} — drop captures in; Ctrl+C stops" +
                       (sessionPath is not null ? " (progress persists to the session)." : "."));

        bool cancelled = false;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancelled = true;
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            while (!cancelled)
            {
                var settled = DateTime.UtcNow - TimeSpan.FromMilliseconds(500);
                var fresh = Directory.EnumerateFiles(folder)
                    .Where(IsImageFile)
                    .Where(f => !processed.Contains(f) && File.GetLastWriteTimeUtc(f) < settled)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (fresh.Count > 0)
                {
                    processed.UnionWith(fresh);
                    bool added = false;
                    foreach (var s in services.Decoder.CollectShards(fresh, @out.WriteLine))
                        if (seen.Add((s.Header.FileId, s.Header.Index, s.Header.IsParity)))
                        {
                            shards.Add(s);
                            added = true;
                        }
                    if (added)
                    {
                        if (sessionPath is not null)
                            services.Sessions.Save(sessionPath, shards);
                        PrintSetStatus(@out, shards, services.Parity);
                        if (services.Parity.IsSetComplete(shards))
                        {
                            var restored = services.Assembler.Assemble(shards, outputPath, @out.WriteLine, password);
                            @out.WriteLine($"Restored {restored.Count} file(s).");
                            if (sessionPath is not null && File.Exists(sessionPath))
                                File.Delete(sessionPath);
                            return 0;
                        }
                    }
                }
                Thread.Sleep(pollMs);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        if (sessionPath is not null && shards.Count > 0)
        {
            services.Sessions.Save(sessionPath, shards);
            @out.WriteLine($"Stopped — {shards.Count} shard(s) saved to {sessionPath}.");
        }
        return 3;
    }

    /// <summary>Union of two shard lists, first occurrence of each (file, index, parity) winning.</summary>
    private static List<DecodedShard> MergeShards(List<DecodedShard> first, List<DecodedShard> second)
    {
        var merged = new List<DecodedShard>(first.Count + second.Count);
        var seen = new HashSet<(ulong, int, bool)>();
        foreach (var s in first.Concat(second))
            if (seen.Add((s.Header.FileId, s.Header.Index, s.Header.IsParity)))
                merged.Add(s);
        return merged;
    }

    private static void PrintSetStatus(TextWriter @out, List<DecodedShard> shards, IParityReassembler parity)
    {
        foreach (var group in shards.GroupBy(s => s.Header.FileId))
        {
            var first = group.First().Header;
            var have = group.Where(s => !s.Header.IsParity).Select(s => s.Header.Index).ToHashSet();
            var missing = Enumerable.Range(0, first.Count).Where(i => !have.Contains(i)).ToList();
            int parityCount = group.Count(s => s.Header.IsParity);
            bool complete = parity.IsSetComplete([.. group]);
            string detail = missing.Count == 0
                ? "all data present"
                : $"missing image(s) {string.Join(", ", missing.Take(20).Select(i => i + 1))}{(missing.Count > 20 ? ", ..." : "")}";
            @out.WriteLine($"  '{first.FileName}': {have.Count}/{first.Count} data + {parityCount} parity — " +
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

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".png" or ".apng" or ".bmp" or ".jpg" or ".jpeg" or ".webp" or ".tga" or ".qoi" or ".tif" or ".tiff";

    private static (List<string> Positional, Dictionary<string, string> Named, HashSet<string> Flags) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--no-compress" or "--camera" or "--video" or "--json" or "--watch" or "--screen" or "--open" or "--interleave2" or "--clipboard" or "--dry-run")
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

    /// <summary>Recognized options (take a value) and flags (boolean) per subcommand. The single
    /// source of truth for option validation; keep in sync with each handler's Get/flags calls.</summary>
    private sealed record ArgSpec(string[] Options, string[] Flags);

    private static readonly Dictionary<string, ArgSpec> ArgSpecs = new()
    {
        ["encode"] = new(
            ["-o", "--out", "-r", "--resolution", "-c", "--cell", "-b", "--bits", "-e", "--ecc",
             "-R", "--recovery", "-F", "--fountain", "-f", "--format", "-p", "--password",
             "-i", "--interval", "--slideshow", "--profile"],
            ["--json", "--camera", "--no-compress", "--interleave2", "--video", "--open", "--dry-run"]),
        // `test <file> [encode opts]` shares the encode density surface (BuildEncodeOptions reads
        // all of these), plus --camera. No --json: the test emits only a human verdict.
        ["test"] = new(
            ["-r", "--resolution", "-c", "--cell", "-b", "--bits", "-e", "--ecc", "-R", "--recovery",
             "-F", "--fountain", "-p", "--password", "-f", "--format", "--profile"],
            ["--camera", "--no-compress", "--interleave2"]),
        ["decode"] = new(["-o", "--out", "-p", "--password", "--session", "--fps"], ["--clipboard", "--watch"]),
        ["verify"] = new(["--session"], ["--json"]),
        ["info"] = new(["--heatmap"], ["--json"]),
        ["receive"] = new(["-o", "--out", "-p", "--password", "--region", "--device", "--format", "--fps"], ["--screen"]),
        ["calibrate"] = new(["-o", "--out", "-r", "--resolution"], ["--camera"]),
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
        // `--recovery --camera` silently drops --recovery). Skip -p/--password: a password may
        // legitimately start with '-'.
        foreach (var (key, val) in named)
            if (key is not ("-p" or "--password") && val.Length > 1 && val[0] == '-' && known.Contains(val))
                return $"option '{key}' is missing a value ('{val}' is another option).";

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
        AppSettings.EncodeDefaultSettings defaults, bool camera, int width, int height) => new()
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
        Password = Get(named, "-p", "--password"),
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
        @out.WriteLine($"Dry run — no images written. This encode would produce, in {outDir}:");
        string split = plan.ParityImages > 0
            ? $"{plan.DataImages} data + {plan.ParityImages} recovery image(s)"
            : $"{plan.DataImages} data image(s)";
        @out.WriteLine($"  {plan.ImageCount} image(s) of {plan.Width}x{plan.Height}px ({split}), up to {plan.BytesPerImage:N0} payload bytes each, format {plan.Format}.");
        if (plan.ImageCount > DryRunImageWarnThreshold)
            @out.WriteLine($"  note: {plan.ImageCount} images is a lot to capture — raise density with a larger --resolution, bigger --cell, or more --bits, or lower --recovery.");
        return 0;
    }

    private static int Help(TextWriter @out, TextWriter err, string? error = null)
    {
        if (error is not null)
            err.WriteLine($"error: {error}\n");
        @out.WriteLine(
            """
            QrShard — encode any file into dense QR-style images and back.

            usage:
              qrshard encode <file|folder> [options]   Split a file (or a folder, tar-ed
                                         automatically and extracted on decode) into shard images.
                -o, --out <dir>          Output folder (default: <file>.shards next to the input)
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
                                         rebuilt without recapture; pct% extra images, 0-100
                                         (default: 0; e.g. 15 tolerates losing ~15% of the images)
                -F, --fountain <pct>     Fountain coding for video mode: pct% extra CODED frames
                                         (random linear combinations); ANY enough captured frames
                                         per stripe reconstruct the data — ideal with --video,
                                         where torn/glared frames simply don't count
                -p, --password <pw>      AES-256-GCM encrypt the payload; decode needs the same
                                         password (wrong password fails cleanly, nothing leaks)
                -f, --format <fmt>       Lossless image format: png, bmp, tga, qoi, webp, tiff
                                         (default: png, written by the built-in fast PNG writer)
                --camera                 Camera profile: adds finder patterns so images decode
                                         from PHOTOS of the screen (rotation + perspective), not
                                         just screenshots; shifts defaults to cell 8, 2 bits,
                                         ECC 32 (explicit flags still win). Far lower density —
                                         use for small/medium payloads
                --video                  Also write slideshow.html: one self-contained page that
                                         cycles the images forever — record the screen for a
                                         full cycle instead of screenshotting by hand
                -i, --interval <ms>      Slideshow interval per image (default: 500, min 100)
                --slideshow <kind>       With --video: "html" (default) or "apng" (a single
                                         animated PNG cycling the shards)
                --interleave2            v2 permuted interleave: spreads VERTICAL damage as well
                                         as horizontal (needs ECC; older decoders reject it)
                --profile <name>         Apply a named encode preset from appsettings.json
                                         (flags still override it)
                --json                   Emit the encode result as JSON on stdout
                --dry-run                Print the image count and geometry without writing any
                                         images (preview before a folder emits hundreds of PNGs)
                --no-compress            Skip compression of the payload
                Multiple inputs (files and/or folders) are bundled into one archive and
                extracted on decode: qrshard encode a.bin b.bin docs/ -o out.shards
              qrshard send <file|folder...> [encode options]
                                         One-step sender: encode with a slideshow and open it
                                         in the default browser

              qrshard decode <folder|images...|recording> [-o <file>]
                                         Reconstitute the original file from captured images, or
                                         from a screen/phone RECORDING of the slideshow
                                         (mp4/webm/mkv/mov/avi need ffmpeg on PATH; animated
                                         png/gif/webp decode natively)
                --fps <n>                Frame extraction rate for video files (default: 8)
                -p, --password <pw>      Password for encrypted payloads
                --session <file>         Accumulate shards across capture sittings: incomplete
                                         sets persist to the session file (exit code 3) and the
                                         next run resumes from the union; deleted on success
                --watch                  Keep watching the folder: decode captures as they land
                                         and assemble the moment the set completes (Ctrl+C
                                         stops; progress persists when --session is given)
                --clipboard              (Windows) decode the bitmap on the clipboard —
                                         Win+Shift+S a displayed shard, no file saving;
                                         accumulates with --session
              qrshard receive [--device d] [--screen] [--region x,y,w,h] [--fps n] [-o f] [-p pw]
                                         LIVE receiver: decode a webcam pointed at the sender's
                                         slideshow — or, with --screen, THIS machine's own
                                         screen (put the slideshow in an RDP/VM window and
                                         transfer out of locked-down remotes). Stops
                                         automatically when the transfer completes.
                                         (Windows webcams: --device "<name>"; list with
                                         ffmpeg -list_devices true -f dshow -i dummy)
              qrshard calibrate [-o dir] [-r res] [--camera]
                                         Write a ladder of density probes (--camera for the
                                         photo-capture ladder); capture them like a real
                                         transfer, then run qrshard calibrate <folder> to get
                                         recommended -c/-b settings for YOUR setup
              qrshard verify <folder|images...> [--session f] [--json]
                                         Report per-file completeness (missing images, parity
                                         coverage) without writing output; exit 0 when complete
                                         (--json for machine-readable output, also on info)
              qrshard info <image> [--heatmap <out.png>]
                                         Show and validate a single shard image; --heatmap renders
                                         a per-cell ECC damage map (green=clean, red=corrected,
                                         dark red=beyond correction) even for failed decodes
              qrshard test [<file> [encode opts]]
                                         With no file: built-in round-trip self-test. With a file:
                                         encode YOUR file at YOUR settings (-c/-b/-e/--camera/...),
                                         run it through simulated screenshots, and report whether
                                         it survives and how much ECC headroom it used.

            Density guide (per image, after default ECC): bytes ≈ cells x bits/cell / 8 x 0.94.
              Robust default (2160px, cell 3, 4 bits) ≈ 216 KB/image.
              Pixel-perfect captures can push cell 1-2 and 6-8 bits for multi-MB images.
            Capture tips: screenshot the image displayed at 100% zoom; include the full black
            frame with some white margin; avoid fractional display scaling for cell sizes < 3.
            ECC absorbs localized damage (cursor, notification toast, mild JPEG artifacts).
            """);
        return error is null ? 0 : 2;
    }
}
