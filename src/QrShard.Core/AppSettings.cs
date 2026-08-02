using System.IO.Compression;
using System.Text.Json;

namespace QrShard;

/// <summary>
/// Optional settings loaded from appsettings.json next to the executable. Comments and
/// trailing commas in the file are allowed (parsed with <see cref="JsonCommentHandling.Skip"/>,
/// matching the behavior of the standard .NET configuration stack). A missing file or a
/// missing setting means the default; a malformed file or an invalid value is a hard error —
/// silently falling back would hide a typo from the user.
///
/// Only preferences and machine tuning live here. Protocol constants (frame geometry, metadata
/// layout, magic numbers, RS/GF parameters) are deliberately compiled in: both sides of a
/// transfer must agree on them, so they must not vary per machine.
/// </summary>
internal sealed class AppSettings
{
    private static readonly HashSet<string> RootSettings = new(StringComparer.Ordinal)
    {
        "PngCompressionLevel", "PayloadCompressionLevel", "ShardFolderSuffix",
        "EncodeMemoryBudgetMB", "DecodeMaxParallelism", "DecodeMemoryBudgetMB",
        "ReceiveFps", "WatchPollMs", "ReceiveDecodeWorkers", "FfmpegPath",
        "EncodeDefaults", "EncodeProfiles",
    };

    private static readonly HashSet<string> EncodeSettings = new(StringComparer.Ordinal)
    {
        "Resolution", "CellPx", "BitsPerCell", "EccParity", "RecoveryPercent", "ImageFormat", "Compress",
    };

    private static readonly Lazy<AppSettings> Cached = new(() => Load(DefaultPath));

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Current => Cached.Value;

    /// <summary>
    /// Configuration-free defaults for library callers. Only the CLI opts into the adjacent
    /// appsettings.json file; embedding QrShard must not parse an unrelated host configuration.
    /// </summary>
    public static AppSettings BuiltIn { get; } = new();

    /// <summary>CLI defaults for `encode`; each is overridden by its flag when given.</summary>
    public EncodeDefaultSettings EncodeDefaults { get; private set; } = new();

    /// <summary>Suffix of the shard folder created next to the input when -o is not given.</summary>
    public string ShardFolderSuffix { get; private set; } = ".shards";

    /// <summary>
    /// Deflate level for the built-in PNG writer, applied where compression pays off
    /// (cell sizes >= 2 px). See appsettings.json for the possible values.
    /// </summary>
    public CompressionLevel PngCompressionLevel { get; private set; } = CompressionLevel.Optimal;

    /// <summary>Compression level passed to BrotliStream for the file payload.</summary>
    public CompressionLevel PayloadCompressionLevel { get; private set; } = CompressionLevel.Optimal;

    /// <summary>Planning budget (MB) for resident payload/parity buffers and encode canvases.</summary>
    public int EncodeMemoryBudgetMB { get; private set; } = 2000;

    /// <summary>
    /// Upper bound on parallel image decodes; 0 = automatic (cores, capped at 24). Actual workers
    /// are also reduced by image count and <see cref="DecodeMemoryBudgetMB"/>.
    /// </summary>
    public int DecodeMaxParallelism { get; private set; }

    /// <summary>
    /// Memory budget (MB) for the decoder's per-worker scratch buffers — the counterpart to
    /// <see cref="EncodeMemoryBudgetMB"/>, which the decode side did not have.
    ///
    /// A 4K frame is planned at about 332 MB and a 48-megapixel phone photo at about 1.92 GB, so
    /// the default affords about 12 workers at 4K and 2 on phone-sized photos. The estimate includes
    /// the adaptive binarizer's two 8-byte-per-pixel integral images and measured camera-fallback
    /// overhead. Under-counting is the dangerous direction because input dimensions are chosen by
    /// the sender. This setting throttles concurrency; it is not a hard process-memory ceiling or a
    /// reading of currently free memory, so machines with less RAM to spare should lower it. A
    /// separate pre-load admission check charges one image at about six bytes/pixel against this
    /// same setting (roughly the two RGB24 surfaces needed on the clean path).
    /// </summary>
    public int DecodeMemoryBudgetMB { get; private set; } = 4000;

    /// <summary>Default frame rate for the live receiver (`qrshard receive`).</summary>
    public double ReceiveFps { get; private set; } = 10;

    /// <summary>Poll interval (ms) for watch-mode decoding.</summary>
    public int WatchPollMs { get; private set; } = 250;

    /// <summary>Parallel frame-decode workers for the live receiver; 0 = automatic.</summary>
    public int ReceiveDecodeWorkers { get; private set; }

    /// <summary>Optional absolute path to ffmpeg. When absent, a safe absolute PATH lookup is used.</summary>
    public string? FfmpegPath { get; private set; }

    /// <summary>Named encode presets applied by <c>--profile &lt;name&gt;</c>; flags still override.</summary>
    public IReadOnlyDictionary<string, EncodeDefaultSettings> EncodeProfiles { get; private set; } =
        new Dictionary<string, EncodeDefaultSettings>();

    internal sealed class EncodeDefaultSettings
    {
        public string Resolution { get; set; } = "auto";
        public int CellPx { get; set; } = 3;
        public int BitsPerCell { get; set; } = 4;
        public int EccParity { get; set; } = 16;
        public int RecoveryPercent { get; set; }
        public string ImageFormat { get; set; } = ShardImageFormat.Default;
        public bool Compress { get; set; } = true;
    }

    internal static AppSettings Load(string path)
    {
        var settings = new AppSettings();
        if (!File.Exists(path))
            return settings;
        string file = Path.GetFileName(path);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{file} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{file}: the JSON root must be an object.");
            ValidateKnownProperties(root, RootSettings, "");

            settings.PngCompressionLevel = ReadLevel(root, "PngCompressionLevel", settings.PngCompressionLevel);
            settings.PayloadCompressionLevel = ReadLevel(root, "PayloadCompressionLevel", settings.PayloadCompressionLevel);

            settings.ShardFolderSuffix = ReadString(root, "ShardFolderSuffix", settings.ShardFolderSuffix);
            if (string.IsNullOrWhiteSpace(settings.ShardFolderSuffix) ||
                settings.ShardFolderSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw Invalid("ShardFolderSuffix", settings.ShardFolderSuffix, "a non-empty filename-safe suffix like \".shards\"");

            settings.EncodeMemoryBudgetMB = ReadInt(root, "EncodeMemoryBudgetMB", settings.EncodeMemoryBudgetMB);
            if (settings.EncodeMemoryBudgetMB is < 64 or > 1_000_000)
                throw Invalid("EncodeMemoryBudgetMB", settings.EncodeMemoryBudgetMB, "64-1000000");

            settings.DecodeMaxParallelism = ReadInt(root, "DecodeMaxParallelism", settings.DecodeMaxParallelism);
            if (settings.DecodeMaxParallelism is < 0 or > 1024)
                throw Invalid("DecodeMaxParallelism", settings.DecodeMaxParallelism, "0 (auto) to 1024");

            settings.DecodeMemoryBudgetMB = ReadInt(root, "DecodeMemoryBudgetMB", settings.DecodeMemoryBudgetMB);
            if (settings.DecodeMemoryBudgetMB is < 64 or > 1_000_000)
                throw Invalid("DecodeMemoryBudgetMB", settings.DecodeMemoryBudgetMB, "64-1000000");

            if (root.TryGetProperty("ReceiveFps", out var receiveFps))
            {
                settings.ReceiveFps = receiveFps.ValueKind == JsonValueKind.Number
                    ? receiveFps.GetDouble()
                    : throw Invalid("ReceiveFps", receiveFps.ToString(), "a number of frames per second");
                if (!double.IsFinite(settings.ReceiveFps) || settings.ReceiveFps is <= 0 or > 120)
                    throw Invalid("ReceiveFps", settings.ReceiveFps, "0-120 frames per second");
            }

            settings.WatchPollMs = ReadInt(root, "WatchPollMs", settings.WatchPollMs);
            if (settings.WatchPollMs is < 50 or > 60_000)
                throw Invalid("WatchPollMs", settings.WatchPollMs, "50-60000 milliseconds");

            settings.ReceiveDecodeWorkers = ReadInt(root, "ReceiveDecodeWorkers", settings.ReceiveDecodeWorkers);
            if (settings.ReceiveDecodeWorkers is < 0 or > 64)
                throw Invalid("ReceiveDecodeWorkers", settings.ReceiveDecodeWorkers, "0 (auto) to 64");

            if (root.TryGetProperty("FfmpegPath", out var ffmpegPath))
            {
                if (ffmpegPath.ValueKind != JsonValueKind.String)
                    throw Invalid("FfmpegPath", ffmpegPath.ToString(), "an absolute filesystem path");
                string value = ffmpegPath.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                    throw Invalid("FfmpegPath", value, "an absolute filesystem path");
                settings.FfmpegPath = Path.GetFullPath(value);
            }

            if (root.TryGetProperty("EncodeDefaults", out var defaults))
            {
                if (defaults.ValueKind != JsonValueKind.Object)
                    throw Invalid("EncodeDefaults", defaults.ToString(), "an object of encode settings");
                ParseEncodeSettings(defaults, settings.EncodeDefaults, "EncodeDefaults", Invalid);
            }

            if (root.TryGetProperty("EncodeProfiles", out var profiles))
            {
                if (profiles.ValueKind != JsonValueKind.Object)
                    throw Invalid("EncodeProfiles", profiles.ToString(), "an object of named profiles");
                var parsed = new Dictionary<string, EncodeDefaultSettings>(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in profiles.EnumerateObject())
                {
                    if (profile.Value.ValueKind != JsonValueKind.Object)
                        throw Invalid($"EncodeProfiles.{profile.Name}", profile.Value.ToString(), "an object of encode settings");
                    // Each profile starts from the resolved EncodeDefaults, so a preset only
                    // states the fields it changes.
                    var p = Clone(settings.EncodeDefaults);
                    ParseEncodeSettings(profile.Value, p, $"EncodeProfiles.{profile.Name}", Invalid);
                    parsed[profile.Name] = p;
                }
                settings.EncodeProfiles = parsed;
            }
        }
        return settings;

        InvalidOperationException Invalid(string setting, object value, string expected) =>
            new($"{file}: invalid {setting} '{value}'. Possible values: {expected}.");
    }

    private static EncodeDefaultSettings Clone(EncodeDefaultSettings s) => new()
    {
        Resolution = s.Resolution,
        CellPx = s.CellPx,
        BitsPerCell = s.BitsPerCell,
        EccParity = s.EccParity,
        RecoveryPercent = s.RecoveryPercent,
        ImageFormat = s.ImageFormat,
        Compress = s.Compress,
    };

    private static void ParseEncodeSettings(JsonElement obj, EncodeDefaultSettings d, string prefix,
        Func<string, object, string, InvalidOperationException> invalid)
    {
        ValidateKnownProperties(obj, EncodeSettings, prefix + ".");
        d.Resolution = ReadString(obj, "Resolution", d.Resolution);
        if (!IsValidResolution(d.Resolution))
            throw invalid($"{prefix}.Resolution", d.Resolution, "\"2160\" or \"3840x2160\" style");

        d.CellPx = ReadInt(obj, "CellPx", d.CellPx);
        if (d.CellPx is < 1 or > Layout.MaxCellPx)
            throw invalid($"{prefix}.CellPx", d.CellPx, $"1-{Layout.MaxCellPx}");

        d.BitsPerCell = ReadInt(obj, "BitsPerCell", d.BitsPerCell);
        if (d.BitsPerCell is < Palette.MinBits or > Palette.MaxBits)
            throw invalid($"{prefix}.BitsPerCell", d.BitsPerCell, $"{Palette.MinBits}-{Palette.MaxBits}");

        d.EccParity = ReadInt(obj, "EccParity", d.EccParity);
        if (d.EccParity is < 0 or > Fec.MaxParity || (d.EccParity & 1) != 0)
            throw invalid($"{prefix}.EccParity", d.EccParity, $"an even number 0-{Fec.MaxParity}");

        d.RecoveryPercent = ReadInt(obj, "RecoveryPercent", d.RecoveryPercent);
        if (d.RecoveryPercent is < 0 or > ShardEncoder.MaxRecoveryPercent)
            throw invalid($"{prefix}.RecoveryPercent", d.RecoveryPercent, $"0-{ShardEncoder.MaxRecoveryPercent}");

        string format = ReadString(obj, "ImageFormat", d.ImageFormat);
        try
        {
            d.ImageFormat = new ShardImageFormat().Normalize(format);
        }
        catch (ArgumentException)
        {
            throw invalid($"{prefix}.ImageFormat", format, string.Join(", ", ShardImageFormat.Supported));
        }

        d.Compress = ReadBool(obj, "Compress", d.Compress);
    }

    private static bool IsValidResolution(string value)
    {
        if (value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            return true;
        int split = value.IndexOfAny(['x', 'X']);
        if (split < 0)
            return int.TryParse(value, out int r) && r is >= Layout.MinResolution and <= Layout.MaxResolution;
        return int.TryParse(value[..split], out int w) &&
               int.TryParse(value[(split + 1)..], out int h) &&
               w is >= Layout.MinResolution and <= Layout.MaxResolution &&
               h is >= Layout.MinResolution and <= Layout.MaxResolution;
    }

    private static void ValidateKnownProperties(JsonElement obj, HashSet<string> known, string prefix)
    {
        foreach (var property in obj.EnumerateObject())
            if (!known.Contains(property.Name))
                throw new InvalidOperationException(
                    $"appsettings.json: unknown setting '{prefix}{property.Name}'. Check its spelling.");
    }

    private static CompressionLevel ReadLevel(JsonElement parent, string name, CompressionLevel fallback)
    {
        if (!parent.TryGetProperty(name, out var element))
            return fallback;
        if (element.ValueKind != JsonValueKind.String)
            throw WrongType(name, "a string", element);
        string value = element.GetString() ?? "";
        if (!Enum.TryParse(value, ignoreCase: true, out CompressionLevel parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException(
                $"appsettings.json: invalid {name} '{value}'. " +
                "Possible values: Optimal, Fastest, SmallestSize, NoCompression.");
        return parsed;
    }

    private static string ReadString(JsonElement parent, string name, string fallback)
    {
        if (!parent.TryGetProperty(name, out var element))
            return fallback;
        if (element.ValueKind != JsonValueKind.String)
            throw WrongType(name, "a string", element);
        return element.GetString() ?? throw WrongType(name, "a non-null string", element);
    }

    private static int ReadInt(JsonElement parent, string name, int fallback)
    {
        if (!parent.TryGetProperty(name, out var element))
            return fallback;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            throw WrongType(name, "a 32-bit integer", element);
        return value;
    }

    private static bool ReadBool(JsonElement parent, string name, bool fallback)
    {
        if (!parent.TryGetProperty(name, out var element))
            return fallback;
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw WrongType(name, "true or false", element);
        return element.GetBoolean();
    }

    private static InvalidOperationException WrongType(string name, string expected, JsonElement actual) =>
        new($"appsettings.json: invalid {name} JSON value '{actual}'. Expected {expected}.");
}
