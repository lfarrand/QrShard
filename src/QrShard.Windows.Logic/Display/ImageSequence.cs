namespace QrShard.Display;

/// <summary>Enumerates Display-supported stills in filename order.</summary>
internal static class ImageSequence
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    public static List<string> Enumerate(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
}
