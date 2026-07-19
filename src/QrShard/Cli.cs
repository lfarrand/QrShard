using Microsoft.Extensions.DependencyInjection;

namespace QrShard;

/// <summary>Command handlers with their dependencies resolved from the composition root.</summary>
internal sealed record CliServices(
    IShardEncoder Encoder, IShardDecoder Decoder, IVideoDecoder VideoDecoder,
    ISlideshowWriter Slideshow, ISelfTest SelfTest);

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
                provider.GetRequiredService<ISelfTest>());
            return RunCore(args, @out, err, cfg, services);
        }
        catch (ShardDecodeException ex)
        {
            err.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or FormatException or OverflowException)
        {
            err.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunCore(string[] args, TextWriter @out, TextWriter err, AppSettings settings, CliServices services)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return Help(@out, err);

        switch (args[0].ToLowerInvariant())
        {
            case "encode":
            {
                var (positional, named, flags) = ParseArgs(args[1..]);
                if (positional.Count != 1)
                    return Help(@out, err, "encode requires exactly one input file.");
                string file = positional[0];
                if (!File.Exists(file))
                    return Help(@out, err, $"file not found: {file}");

                // Flag > appsettings.json EncodeDefaults > built-in default. The camera profile
                // swaps in photo-appropriate density defaults (big cells, few colors, heavy
                // ECC); explicit flags still win.
                var defaults = settings.EncodeDefaults;
                bool camera = flags.Contains("--camera");
                string resolutionValue = Get(named, "-r", "--resolution") ?? defaults.Resolution;
                var (width, height, resolutionNote) = ResolveResolution(resolutionValue);
                var opt = new EncodeOptions
                {
                    Width = width,
                    Height = height,
                    CellPx = GetInt(named, "-c", "--cell", camera ? 8 : defaults.CellPx),
                    BitsPerCell = GetInt(named, "-b", "--bits", camera ? 2 : defaults.BitsPerCell),
                    EccParity = GetInt(named, "-e", "--ecc", camera ? 32 : defaults.EccParity),
                    RecoveryPercent = GetInt(named, "-R", "--recovery", defaults.RecoveryPercent),
                    ImageFormat = Get(named, "-f", "--format") ?? defaults.ImageFormat,
                    Compress = !flags.Contains("--no-compress") && defaults.Compress,
                    CameraMode = camera,
                };
                string outDir = Get(named, "-o", "--out") ?? Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileName(file) + settings.ShardFolderSuffix);

                @out.WriteLine($"Encoding '{file}' → {outDir}");
                @out.WriteLine($"  {opt.Width}x{opt.Height}px{resolutionNote}, cell {opt.CellPx}px, {opt.BitsPerCell} bits/cell, " +
                               $"ECC parity {opt.EccParity}, recovery {opt.RecoveryPercent}%, " +
                               $"format {opt.ImageFormat}, compression {(opt.Compress ? "on" : "off")}" +
                               (camera ? ", camera profile (finder patterns)" : ""));
                var result = services.Encoder.Encode(file, outDir, opt, @out.WriteLine);
                @out.WriteLine($"Done: {result.ImageCount} image(s) of {result.Width}x{result.Height}px, up to {result.BytesPerImage:N0} payload bytes each.");
                if (result.ParityImages > 0)
                    @out.WriteLine($"  {result.DataImages} data + {result.ParityImages} parity image(s); " +
                                   $"can recover up to {result.StripeParity} lost image(s) per {result.StripeData + result.StripeParity}.");

                if (flags.Contains("--video"))
                {
                    int intervalMs = GetInt(named, "-i", "--interval", SlideshowWriter.DefaultIntervalMs);
                    string slideshow = services.Slideshow.Write(outDir, result.Files, intervalMs);
                    @out.WriteLine($"Slideshow: {slideshow} ({intervalMs} ms/image, ~{result.ImageCount * intervalMs / 1000.0:0.#} s per cycle).");
                    @out.WriteLine("  Open it in a browser, press F11, and record the screen for at least one full cycle.");
                }
                return 0;
            }

            case "decode":
            {
                var (positional, named, _) = ParseArgs(args[1..]);
                if (positional.Count == 0)
                    return Help(@out, err, "decode requires a folder, image files, or a video recording.");

                // A single video file (or animated image) is a recording of the slideshow.
                if (positional.Count == 1 && File.Exists(positional[0]) &&
                    (VideoDecoder.IsVideoFile(positional[0]) ||
                     (IsImageFile(positional[0]) && VideoDecoder.IsAnimatedImage(positional[0]))))
                {
                    double fps = GetDouble(named, "--fps", 8.0);
                    @out.WriteLine($"Decoding video '{positional[0]}' (extracting at {fps} fps)...");
                    var fromVideo = services.VideoDecoder.Decode(positional[0], Get(named, "-o", "--out"), fps, @out.WriteLine, out _);
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

                @out.WriteLine($"Decoding {images.Count} image(s)...");
                var restored = services.Decoder.DecodeFolder(images, Get(named, "-o", "--out"), @out.WriteLine);
                @out.WriteLine($"Restored {restored.Count} file(s).");
                return 0;
            }

            case "info":
            {
                var (positional, _, _) = ParseArgs(args[1..]);
                if (positional.Count != 1 || !File.Exists(positional[0]))
                    return Help(@out, err, "info requires one shard image.");
                var shard = services.Decoder.DecodeImage(positional[0], new DecodeScratch());
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

            case "test":
                return services.SelfTest.Run() ? 0 : 1;

            default:
                return Help(@out, err, $"unknown command: {args[0]}");
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
            is ".png" or ".bmp" or ".jpg" or ".jpeg" or ".webp" or ".tga" or ".qoi" or ".tif" or ".tiff";

    private static (List<string> Positional, Dictionary<string, string> Named, HashSet<string> Flags) ParseArgs(string[] args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--no-compress" or "--camera" or "--video")
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

    private static int Help(TextWriter @out, TextWriter err, string? error = null)
    {
        if (error is not null)
            err.WriteLine($"error: {error}\n");
        @out.WriteLine(
            """
            QrShard — encode any file into dense QR-style images and back.

            usage:
              qrshard encode <file> [options]     Split a file into shard images.
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
                --no-compress            Skip deflate compression of the payload

              qrshard decode <folder|images...|recording> [-o <file>]
                                         Reconstitute the original file from captured images, or
                                         from a screen/phone RECORDING of the slideshow
                                         (mp4/webm/mkv/mov/avi need ffmpeg on PATH; animated
                                         png/gif/webp decode natively)
                --fps <n>                Frame extraction rate for video files (default: 8)
              qrshard info <image>       Show and validate a single shard image.
              qrshard test               Round-trip self-test, including simulated screenshots.

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
