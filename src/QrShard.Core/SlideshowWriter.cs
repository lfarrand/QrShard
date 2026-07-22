using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

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
internal sealed class SlideshowWriter : ISlideshowWriter
{
    public const int DefaultIntervalMs = 500;
    public const int MinIntervalMs = 100;

    /// <summary>
    /// Writes slideshow.apng next to the shards: a single animated PNG cycling every image with
    /// the given per-frame delay. Loops forever. An alternative to the HTML page for viewers
    /// that prefer one media file (image viewers, video capture setups) — every frame is a
    /// lossless, exact copy of its shard, so the recorded output decodes identically.
    /// </summary>
    public string WriteApng(string outDir, IReadOnlyList<string> imageFiles, int intervalMs)
    {
        if (intervalMs < MinIntervalMs)
            throw new ArgumentException($"Slideshow interval must be at least {MinIntervalMs} ms.");
        if (imageFiles.Count == 0)
            throw new ArgumentException("No shard images to build a slideshow from.");

        using var animation = Image.Load<Rgb24>(imageFiles[0]);
        SetFrameTiming(animation.Frames.RootFrame.Metadata.GetPngMetadata(), intervalMs);
        for (int i = 1; i < imageFiles.Count; i++)
        {
            using var next = Image.Load<Rgb24>(imageFiles[i]);
            var frame = animation.Frames.AddFrame(next.Frames.RootFrame);
            SetFrameTiming(frame.Metadata.GetPngMetadata(), intervalMs);
        }

        var pngMeta = animation.Metadata.GetPngMetadata();
        pngMeta.RepeatCount = 0; // 0 = loop forever
        string path = Path.Combine(outDir, "slideshow.apng");
        animation.Save(path, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
        return path;
    }

    /// <summary>Each shard fully replaces the previous frame — Source blend (overwrite, never
    /// alpha-composite) with background disposal — so every recorded frame is an exact copy.</summary>
    private static void SetFrameTiming(PngFrameMetadata meta, int intervalMs)
    {
        meta.FrameDelay = new SixLabors.ImageSharp.Rational((uint)intervalMs, 1000);
        meta.BlendMode = FrameBlendMode.Source;
        meta.DisposalMode = FrameDisposalMode.RestoreToBackground;
    }

    /// <summary>Writes slideshow.html next to the shard images; returns its path.</summary>
    public string Write(string outDir, IReadOnlyList<string> imageFiles, int intervalMs)
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
            var (mime, bytes) = EmbedFrame(file);
            sb.Append("\"data:").Append(mime).Append(";base64,");
            sb.Append(Convert.ToBase64String(bytes));
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

    /// <summary>
    /// Returns the (MIME, bytes) to embed for one shard. Browsers render png/bmp/webp/gif via an
    /// &lt;img&gt; data URI, but not tga/qoi/tiff — so those are transcoded to a lossless PNG (a
    /// pixel-exact copy that decodes identically). Without this the HTML slideshow silently shows
    /// nothing for half the supported encode formats, and a screen recording of it transmits
    /// nothing. (WriteApng already re-encodes every frame, so only this HTML path needed it.)
    /// </summary>
    private static (string Mime, byte[] Bytes) EmbedFrame(string file)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".png": return ("image/png", File.ReadAllBytes(file));
            case ".bmp": return ("image/bmp", File.ReadAllBytes(file));
            case ".webp": return ("image/webp", File.ReadAllBytes(file));
            case ".gif": return ("image/gif", File.ReadAllBytes(file));
            default:
                using (var img = Image.Load<Rgb24>(file))
                using (var ms = new MemoryStream())
                {
                    img.Save(ms, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
                    return ("image/png", ms.ToArray());
                }
        }
    }
}
