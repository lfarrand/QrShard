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
    private static readonly Lazy<AppSettings> Cached = new(() => Load(DefaultPath));

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Current => Cached.Value;

    /// <summary>CLI defaults for `encode`; each is overridden by its flag when given.</summary>
    public EncodeDefaultSettings EncodeDefaults { get; private set; } = new();

    /// <summary>Suffix of the shard folder created next to the input when -o is not given.</summary>
    public string ShardFolderSuffix { get; private set; } = ".shards";

    /// <summary>
    /// Deflate level for the built-in PNG writer, applied where compression pays off
    /// (cell sizes >= 2 px). See appsettings.json for the possible values.
    /// </summary>
    public CompressionLevel PngCompressionLevel { get; private set; } = CompressionLevel.Optimal;

    /// <summary>Deflate level for compressing the file payload itself.</summary>
    public CompressionLevel PayloadCompressionLevel { get; private set; } = CompressionLevel.Optimal;

    /// <summary>Memory budget (MB) for the encoder's per-worker pixel canvases.</summary>
    public int EncodeMemoryBudgetMB { get; private set; } = 2000;

    /// <summary>Max parallel image decodes; 0 = automatic (cores, capped at 16).</summary>
    public int DecodeMaxParallelism { get; private set; }

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
                return settings;

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

            if (root.TryGetProperty("EncodeDefaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
            {
                var d = settings.EncodeDefaults;
                d.Resolution = ReadString(defaults, "Resolution", d.Resolution);
                if (!IsValidResolution(d.Resolution))
                    throw Invalid("EncodeDefaults.Resolution", d.Resolution, "\"2160\" or \"3840x2160\" style");

                d.CellPx = ReadInt(defaults, "CellPx", d.CellPx);
                if (d.CellPx is < 1 or > Layout.MaxCellPx)
                    throw Invalid("EncodeDefaults.CellPx", d.CellPx, $"1-{Layout.MaxCellPx}");

                d.BitsPerCell = ReadInt(defaults, "BitsPerCell", d.BitsPerCell);
                if (d.BitsPerCell is < Palette.MinBits or > Palette.MaxBits)
                    throw Invalid("EncodeDefaults.BitsPerCell", d.BitsPerCell, $"{Palette.MinBits}-{Palette.MaxBits}");

                d.EccParity = ReadInt(defaults, "EccParity", d.EccParity);
                if (d.EccParity is < 0 or > Fec.MaxParity || (d.EccParity & 1) != 0)
                    throw Invalid("EncodeDefaults.EccParity", d.EccParity, $"an even number 0-{Fec.MaxParity}");

                d.RecoveryPercent = ReadInt(defaults, "RecoveryPercent", d.RecoveryPercent);
                if (d.RecoveryPercent is < 0 or > ShardEncoder.MaxRecoveryPercent)
                    throw Invalid("EncodeDefaults.RecoveryPercent", d.RecoveryPercent, $"0-{ShardEncoder.MaxRecoveryPercent}");

                string format = ReadString(defaults, "ImageFormat", d.ImageFormat);
                try
                {
                    d.ImageFormat = new ShardImageFormat().Normalize(format);
                }
                catch (ArgumentException)
                {
                    throw Invalid("EncodeDefaults.ImageFormat", format, string.Join(", ", ShardImageFormat.Supported));
                }

                d.Compress = ReadBool(defaults, "Compress", d.Compress);
            }
        }
        return settings;

        InvalidOperationException Invalid(string setting, object value, string expected) =>
            new($"{file}: invalid {setting} '{value}'. Possible values: {expected}.");
    }

    private static bool IsValidResolution(string value)
    {
        if (value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            return true;
        int split = value.IndexOfAny(['x', 'X']);
        return split < 0
            ? int.TryParse(value, out int r) && r > 0
            : int.TryParse(value[..split], out int w) && w > 0 && int.TryParse(value[(split + 1)..], out int h) && h > 0;
    }

    private static CompressionLevel ReadLevel(JsonElement parent, string name, CompressionLevel fallback)
    {
        if (!parent.TryGetProperty(name, out var element))
            return fallback;
        string value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        if (!Enum.TryParse(value, ignoreCase: true, out CompressionLevel parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException(
                $"appsettings.json: invalid {name} '{value}'. " +
                "Possible values: Optimal, Fastest, SmallestSize, NoCompression.");
        return parsed;
    }

    private static string ReadString(JsonElement parent, string name, string fallback) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement parent, string name, int fallback) =>
        parent.TryGetProperty(name, out var element) && element.TryGetInt32(out int value) ? value : fallback;

    private static bool ReadBool(JsonElement parent, string name, bool fallback) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;
}
