using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace QrShard;

/// <summary>
/// Generates an HTML slideshow that cycles through the adjacent shard images forever — the
/// sender-side half of video mode. The receiver simply records the screen for at
/// least one full cycle (screen recorder or phone video); duplicate, torn, and mid-transition
/// frames are all harmless because every shard is self-describing and CRC/ECC-gated.
///
/// The page stays tiny even for large transfers because it references the files beside it rather
/// than base64-embedding and duplicating every shard in memory. Playback begins only after an
/// explicit Start gesture requests browser fullscreen and removes every control/overlay from the
/// frame. Each next image is decoded off-screen before the visible image is switched.
/// </summary>
internal sealed class SlideshowWriter : ISlideshowWriter
{
    public const int DefaultIntervalMs = 500;
    public const int MinIntervalMs = 100;
    internal const long MaxApngDecodedBytes = 256L * 1024 * 1024;

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

        long decodedBytes = 0;
        long largestFrameBytes = 0;
        var oneFrame = ShardDecoder.NewShardImageDecoderOptions();
        foreach (string file in imageFiles)
        {
            var info = Image.Identify(oneFrame, file);
            long frameBytes = checked((long)info.Width * info.Height * 3);
            decodedBytes = checked(decodedBytes + frameBytes);
            largestFrameBytes = Math.Max(largestFrameBytes, frameBytes);
            // AddFrame clones the currently loaded next frame before that temporary image is
            // disposed. Bound the retained animation plus that extra surface, not just the final
            // frame collection.
            if (checked(decodedBytes + largestFrameBytes) > MaxApngDecodedBytes)
                throw new ArgumentException(
                    $"APNG slideshow would retain more than {MaxApngDecodedBytes / (1024 * 1024)} MiB of decoded frames. " +
                    "Use --slideshow html for a scalable, file-referencing slideshow.");
        }

        string path = Path.Combine(outDir, "slideshow.apng");
        string staging = path + $".qrshard-{Guid.NewGuid():N}.tmp";
        try
        {
            using var animation = Image.Load<Rgb24>(oneFrame, imageFiles[0]);
            SetFrameTiming(animation.Frames.RootFrame.Metadata.GetPngMetadata(), intervalMs);
            for (int i = 1; i < imageFiles.Count; i++)
            {
                using var next = Image.Load<Rgb24>(oneFrame, imageFiles[i]);
                var frame = animation.Frames.AddFrame(next.Frames.RootFrame);
                SetFrameTiming(frame.Metadata.GetPngMetadata(), intervalMs);
            }

            var pngMeta = animation.Metadata.GetPngMetadata();
            pngMeta.RepeatCount = 0; // 0 = loop forever
            animation.Save(staging, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(staging); } catch (IOException) { }
        }
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
        if (imageFiles.Count == 0)
            throw new ArgumentException("No shard images to build a slideshow from.");

        string path = Path.Combine(outDir, "slideshow.html");
        string staging = path + $".qrshard-{Guid.NewGuid():N}.tmp";
        string generation = Guid.NewGuid().ToString("N");
        string? previousGeneration = ReadGeneration(path);
        var newSidecars = new List<string>();
        bool published = false;
        try
        {
            using var writer = new StreamWriter(staging, append: false, new UTF8Encoding(false), 1 << 16);
            writer.Write(
            $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta name="qrshard-generation" content="{{generation}}">
            <title>QrShard slideshow ({{imageFiles.Count}} images, {{intervalMs}} ms)</title>
            <style>
              html, body { margin: 0; height: 100%; background: #fff; overflow: hidden; }
              #shard { width: 100vw; height: 100vh; object-fit: contain; image-rendering: pixelated; display: block; }
              #controls { position: fixed; inset: 0; display: grid; place-content: center; gap: 12px;
                          text-align: center; font: 16px system-ui, sans-serif; color: #111;
                          background: #fffffff2; }
              #controls.hidden { display: none; }
              button { font: inherit; padding: 10px 18px; cursor: pointer; }
            </style></head><body>
            <img id="shard" alt="shard">
            <div id="controls">
              <button id="start" type="button" disabled>Start fullscreen playback</button>
              <div id="status">Loading the first shard…</div>
            </div>
            <script>
            const images = [
            """);

            for (int i = 0; i < imageFiles.Count; i++)
            {
                string displayFile = BrowserRenderableFrame(outDir, imageFiles[i], generation, i);
                if (!string.Equals(displayFile, imageFiles[i], StringComparison.Ordinal))
                    newSidecars.Add(displayFile);
                string relative = Path.GetRelativePath(outDir, displayFile)
                    .Replace(Path.DirectorySeparatorChar, '/');
                string uri = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
                // JsonSerializer's reflection-based overload is trimming/dynamic-code unsafe and
                // breaks NativeAOT release publishing under warnings-as-errors. JsonEncodedText
                // performs the same JSON/JavaScript string escaping without runtime metadata.
                writer.Write('"');
                writer.Write(JsonEncodedText.Encode(uri).ToString());
                writer.Write('"');
                writer.WriteLine(',');
            }

            writer.Write(
            $$"""
            ];
            const interval = {{intervalMs}};
            const shard = document.getElementById("shard");
            const controls = document.getElementById("controls");
            const start = document.getElementById("start");
            const status = document.getElementById("status");
            let i = 0, playing = false, timer = 0;

            function queueNext(skipped = 0) {
              if (!playing) return;
              const nextIndex = (i + 1) % images.length;
              const loader = new Image();
              loader.onload = () => {
                if (!playing) return;
                shard.src = loader.src;
                i = nextIndex;
                timer = window.setTimeout(queueNext, interval);
              };
              loader.onerror = () => {
                // Missing/corrupt sidecars are erasures: advance past them so image-level parity
                // can do its job instead of stalling forever on one unreadable frame.
                i = nextIndex;
                if (skipped + 1 >= images.length)
                  timer = window.setTimeout(() => queueNext(0), interval);
                else
                  queueNext(skipped + 1);
              };
              loader.src = images[nextIndex];
            }

            function loadFirst(index, remaining) {
              if (remaining === 0) {
                status.textContent = "No shard image could be loaded. Restore the image files and reload this page.";
                return;
              }
              const loader = new Image();
              loader.onload = () => {
                shard.src = loader.src;
                i = index;
                start.disabled = false;
                status.textContent = `${images.length} images · {{intervalMs}} ms each. Start recording after the controls disappear.`;
              };
              loader.onerror = () => loadFirst((index + 1) % images.length, remaining - 1);
              loader.src = images[index];
            }

            async function begin() {
              if (playing) return;
              // requestFullscreen requires a user gesture. Even if browser policy denies it,
              // controls are removed before playback so an F11/manual fullscreen recording is
              // still clean.
              try {
                if (!document.fullscreenElement && document.documentElement.requestFullscreen)
                  await document.documentElement.requestFullscreen();
              } catch (_) { }
              controls.className = "hidden";
              playing = true;
              timer = window.setTimeout(queueNext, interval);
            }

            start.addEventListener("click", begin);
            loadFirst(0, images.length);
            </script></body></html>
            """);
            writer.Flush();
            writer.Close();
            File.Move(staging, path, overwrite: true);
            published = true;

            // Remove only the exact generation named by the previous page. A broad wildcard could
            // delete user files or a concurrent writer's not-yet-published sidecars.
            if (previousGeneration is not null && previousGeneration != generation)
            {
                try
                {
                    foreach (string old in Directory.EnumerateFiles(outDir,
                                 $".slideshow-{previousGeneration}-frame-*.png"))
                        try { File.Delete(old); } catch (IOException) { }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Stale sidecars are harmless; the new page is already complete.
                }
            }
        }
        finally
        {
            try { File.Delete(staging); } catch (IOException) { }
            if (!published)
                foreach (string sidecar in newSidecars)
                    try { File.Delete(sidecar); } catch (IOException) { }
        }
        return path;
    }

    private static string? ReadGeneration(string htmlPath)
    {
        if (!File.Exists(htmlPath))
            return null;
        try
        {
            const string marker = "<meta name=\"qrshard-generation\" content=\"";
            using var reader = new StreamReader(htmlPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024);
            var prefix = new char[1024];
            int count = reader.ReadBlock(prefix, 0, prefix.Length);
            string text = new(prefix, 0, count);
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;
            start += marker.Length;
            if (start + 33 > text.Length || text[start + 32] != '\"')
                return null;
            string generation = text.Substring(start, 32);
            return generation.All(Uri.IsHexDigit) ? generation : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a browser-renderable file. Browsers render png/bmp/webp/gif/jpeg directly, but not
    /// tga/qoi/tiff consistently, so those become numbered PNG sidecars beside the page. Processing
    /// one file at a time keeps memory independent of transfer size.
    /// </summary>
    private static string BrowserRenderableFrame(string outDir, string file, string generation, int index)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".png" or ".bmp" or ".webp" or ".gif" or ".jpg" or ".jpeg":
                return file;
            default:
                string sidecar = Path.Combine(outDir, $".slideshow-{generation}-frame-{index + 1:D6}.png");
                using (var img = Image.Load<Rgb24>(ShardDecoder.NewShardImageDecoderOptions(), file))
                    img.Save(sidecar, new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 });
                return sidecar;
        }
    }
}
