using System.IO.Compression;
using System.Text.Json;

namespace QrShard;

/// <summary>
/// Optional settings loaded from appsettings.json next to the executable. Comments and
/// trailing commas in the file are allowed (parsed with <see cref="JsonCommentHandling.Skip"/>,
/// matching the behavior of the standard .NET configuration stack). A missing file or a
/// missing setting means the default; a malformed file or an invalid value is a hard error —
/// silently falling back would hide a typo from the user.
/// </summary>
internal sealed class AppSettings
{
    private static readonly Lazy<AppSettings> Cached = new(() => Load(DefaultPath));

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Current => Cached.Value;

    /// <summary>
    /// Deflate level for the built-in PNG writer, applied where compression pays off
    /// (cell sizes >= 2 px). See appsettings.json for the possible values.
    /// </summary>
    public CompressionLevel PngCompressionLevel { get; private set; } = CompressionLevel.Optimal;

    internal static AppSettings Load(string path)
    {
        var settings = new AppSettings();
        if (!File.Exists(path))
            return settings;

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
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("PngCompressionLevel", out var level))
            {
                string value = level.ValueKind == JsonValueKind.String ? level.GetString() ?? "" : level.ToString();
                if (!Enum.TryParse(value, ignoreCase: true, out CompressionLevel parsed) || !Enum.IsDefined(parsed))
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(path)}: invalid PngCompressionLevel '{value}'. " +
                        "Possible values: Optimal, Fastest, SmallestSize, NoCompression.");
                settings.PngCompressionLevel = parsed;
            }
        }
        return settings;
    }
}
