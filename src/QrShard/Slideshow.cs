using System.Text;

namespace QrShard;

/// <summary>
/// Generates a single self-contained HTML slideshow that cycles through the shard images
/// forever — the sender-side half of video mode. The receiver simply records the screen for at
/// least one full cycle (screen recorder or phone video); duplicate, torn, and mid-transition
/// frames are all harmless because every shard is self-describing and CRC/ECC-gated.
///
/// One file, images base64-embedded, opens in any browser on any OS. The HUD overlay hides
/// automatically in fullscreen so it never damages a recorded shard ('h' toggles it back).
/// </summary>
internal static class Slideshow
{
    public const int DefaultIntervalMs = 500;
    public const int MinIntervalMs = 100;

    /// <summary>Writes slideshow.html next to the shard images; returns its path.</summary>
    public static string Write(string outDir, IReadOnlyList<string> imageFiles, int intervalMs)
    {
        if (intervalMs < MinIntervalMs)
            throw new ArgumentException($"Slideshow interval must be at least {MinIntervalMs} ms.");

        var sb = new StringBuilder();
        sb.Append(
            $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <title>QrShard slideshow ({{imageFiles.Count}} images, {{intervalMs}} ms)</title>
            <style>
              html, body { margin: 0; height: 100%; background: #fff; overflow: hidden; }
              img { width: 100vw; height: 100vh; object-fit: contain; image-rendering: pixelated; display: block; }
              #hud { position: fixed; right: 12px; bottom: 8px; font: 13px system-ui, sans-serif;
                     color: #333; background: #ffffffd0; padding: 3px 9px; border-radius: 4px; }
              #hud.hidden { display: none; }
            </style></head><body>
            <img id="shard" alt="shard">
            <div id="hud"></div>
            <script>
            const images = [
            """);

        foreach (string file in imageFiles)
        {
            string mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".tiff" or ".tif" => "image/tiff",
                _ => "application/octet-stream",
            };
            sb.Append("\"data:").Append(mime).Append(";base64,");
            sb.Append(Convert.ToBase64String(File.ReadAllBytes(file)));
            sb.Append("\",\n");
        }

        sb.Append(
            $$"""
            ];
            const interval = {{intervalMs}};
            const shard = document.getElementById("shard");
            const hud = document.getElementById("hud");
            let i = 0, cycle = 1, hudHidden = false;
            function tick() {
              shard.src = images[i];
              hud.textContent = `image ${i + 1}/${images.length} · cycle ${cycle} · F11 fullscreen · press h to toggle this overlay`;
              i = (i + 1) % images.length;
              if (i === 0) cycle++;
            }
            function updateHud() {
              hud.className = hudHidden || document.fullscreenElement ? "hidden" : "";
            }
            document.addEventListener("fullscreenchange", updateHud);
            document.addEventListener("keydown", e => {
              if (e.key === "h") { hudHidden = !hudHidden; updateHud(); }
            });
            setInterval(tick, interval);
            tick();
            </script></body></html>
            """);

        string path = Path.Combine(outDir, "slideshow.html");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
