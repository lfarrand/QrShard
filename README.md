# QrShard

QrShard transfers files between machines through the screen: it encodes any file into a series
of high-density, QR-style images which are displayed on one machine, captured by screenshot on
another, and reconstituted back into the original file — **bit-for-bit, verified by SHA-256**.

The image format is custom (not QR-standard) and tuned for screen-to-screenshot transfer rather
than camera capture. Because a screenshot is a lossless pixel copy, each image can be vastly
denser than a real QR code: from ~212 KB per image at the robust default up to **~6.5 MB per
image** on a 4K display — so a 100 MB file fits in 22 screenshots and a 300 MB zip in ~65.
Layered error correction absorbs cursors, notification pop-ups, and mild re-encoding; optional
parity images let whole screenshots be lost and rebuilt without recapture.

**Contents:** [Platforms](#supported-platforms) · [How to use](#how-to-use-it) ·
[Options](#commands-and-options) · [Configuration](#configuration-appsettingsjson) ·
[Capacity](#capacity-and-throughput) · [Formats](#image-formats) · [Resilience](#resilience) ·
[Benchmarks](#benchmark-snapshot) · [Design notes](#how-it-works) ·
[Building & testing](#building-and-testing)

## Supported platforms

The codec is pure managed .NET 10 — no native dependencies — and the wire format is
platform-agnostic by construction, so shards encoded on one OS decode on any other (verified:
Windows→Linux and Linux→Windows transfers, including parity-recovering a Linux-encoded set on
Windows).

| Platform | Codec | Monitor auto-detection (`-r auto`) | Benchmark machine spec |
|---|---|---|---|
| Windows (x64) | ✅ | ✅ EnumDisplaySettings (physical pixels, DPI-scaling-proof) | ✅ WMI |
| Linux (x64/arm64) | ✅ verified via WSL | ✅ `xrandr` parsing (X11/XWayland); headless falls back | degraded (OS + .NET + cores) |
| macOS (x64/arm64) | ✅ (managed-only code) | ✅ CoreGraphics Retina pixel dimensions (untested on real hardware) | degraded |

`./publish.ps1` (or `./publish.sh`) produces self-contained single-file binaries for `win-x64`,
`linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64` under `publish/<rid>/` (~72-79 MB each) —
no .NET install needed on either machine.

## How to use it

**On the sending machine:**

```
qrshard encode holiday-photos.zip
```

This creates `holiday-photos.zip.shards/` next to the input, containing numbered images sized to
your primary monitor. Open the folder in any image viewer, display each image fullscreen at
**100% zoom**, and screenshot each one on/for the receiving side (a cropped region capture is
fine — just include the whole black frame with a little margin). For large files add `-R 10` so
up to ~10% of the screenshots can be botched or skipped without redoing anything.

**On the receiving machine**, put the captured screenshots in a folder (any filenames, any
order, duplicates fine) and:

```
qrshard decode captures\ -o holiday-photos.zip
```

Every image is CRC-verified as it's read; damaged captures are repaired by error correction or
rebuilt from parity images, anything unrecoverable is reported by exact part number ("missing
image 7 of 22 — recapture it"), and the final file is verified against a SHA-256 carried inside
the shards. If decode says it succeeded, the file is bit-identical. `qrshard info <image>`
inspects and validates a single capture.

**Video mode — no manual capturing at all.** Add `--video` when encoding and a self-contained
`slideshow.html` is written next to the shards: open it in any browser, press F11, and it
cycles every image forever (default 500 ms each; `--interval` to tune). On the receiving side,
just **record the screen** for one full cycle (or point a phone at it, with `--camera` shards)
and feed the recording straight in:

```
qrshard decode recording.mp4 -o holiday-photos.zip
```

Frames are extracted via ffmpeg (must be on PATH for mp4/webm/mkv/mov/avi; animated
png/gif/webp decode natively), near-duplicate frames are skipped cheaply, torn mid-transition
frames fail checksums harmlessly and come around again next cycle, and decoding **stops early**
the moment the collected set is complete — or merely *recoverable*, letting parity
reconstruction fill whatever the recording hadn't reached yet. No synchronization between the
two machines is needed at any point.

Run from source with `dotnet run --project src/QrShard -c Release -- <command>`, or publish
standalone binaries with `./publish.ps1`.

## Commands and options

| Command | Description |
|---|---|
| `qrshard encode <file> [options]` | Split a file into shard images |
| `qrshard decode <folder\|images...> [-o <file>]` | Reconstitute the original file from captured images |
| `qrshard info <image>` | Show and validate a single shard image |
| `qrshard test` | End-to-end self-test, including simulated screenshots |

### `encode` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `-o, --out <dir>` | any path | `<file>.shards` next to the input | Output folder for the shard images |
| `-r, --resolution <px>` | `auto`; one number (square); `WxH` — 700–16384 per side | `auto` | Image size. `auto` detects the primary monitor's native resolution so shards fill the screen they'll be captured from; smaller explicit values show the code surrounded by padding on the display |
| `-c, --cell <px>` | 1–64 | 3 | Data cell size in pixels. 3 survives fractional display rescaling; 1 doubles-to-quadruples density but needs pixel-perfect captures |
| `-b, --bits <n>` | 1–8 | 4 | Bits per cell (color density): 2ⁿ palette colors. Higher = denser but less tolerant of color distortion |
| `-e, --ecc <n>` | even, 0–64 | 16 | Reed-Solomon parity bytes per 255-byte block. 16 ≈ 6% overhead, fixes 8 damaged bytes/block (cursors, toasts); 0 disables; raise for hostile captures |
| `-R, --recovery <pct>` | 0–100 | 0 (off) | Extra **parity images** as % of data images; any lost/destroyed images up to that budget are rebuilt without recapture |
| `-f, --format <fmt>` | `png`, `bmp`, `tga`, `qoi`, `webp`, `tiff` | `png` | Lossless container format (see [Image formats](#image-formats)) |
| `--camera` | flag | off | Camera profile: adds finder patterns so shards decode from **photos** of the screen (rotation + perspective), not just screenshots; shifts defaults to cell 8 / 2 bits / ECC 32 (explicit flags win). See [Camera capture](#camera-capture) |
| `--video` | flag | off | Also write `slideshow.html`, a self-contained page cycling the images forever — record the screen for one cycle instead of screenshotting by hand |
| `-i, --interval <ms>` | ≥ 100 | 500 | Slideshow interval per image (500 is safe for 30 fps recorders) |
| `--no-compress` | flag | compression on | Skip deflate compression of the payload (it is auto-skipped anyway when a sample shows the file is incompressible) |

### `decode` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `-o, --out <file>` | any path | original filename in the current directory (never overwrites — falls back to `<name>.restored<ext>`) | Where to write the reconstituted file |
| `--fps <n>` | > 0 | 8 | Frame extraction rate when decoding a video recording (ffmpeg required for mp4/webm/mkv/mov/avi) |

## Configuration (appsettings.json)

An optional `appsettings.json` next to the executable holds preferences and machine tuning.
Comments are allowed in it (as in standard .NET appsettings files) and every value is documented
inline there. Precedence: **CLI flag > appsettings.json > built-in default**. Invalid values
fail loudly, naming the setting, rather than silently defaulting.

| Setting | Supported values | Default | Description |
|---|---|---|---|
| `EncodeDefaults.Resolution` | `auto`, number, `WxH` | `auto` | Default for `-r` |
| `EncodeDefaults.CellPx` | 1–64 | 3 | Default for `-c` |
| `EncodeDefaults.BitsPerCell` | 1–8 | 4 | Default for `-b` |
| `EncodeDefaults.EccParity` | even, 0–64 | 16 | Default for `-e` |
| `EncodeDefaults.RecoveryPercent` | 0–100 | 0 | Default for `-R` |
| `EncodeDefaults.ImageFormat` | `png` `bmp` `tga` `qoi` `webp` `tiff` | `png` | Default for `-f` |
| `EncodeDefaults.Compress` | `true`/`false` | `true` | `false` = always `--no-compress` |
| `ShardFolderSuffix` | filename-safe suffix | `.shards` | Output-folder suffix when `-o` isn't given |
| `PngCompressionLevel` | `Optimal`, `Fastest`, `SmallestSize`, `NoCompression` | `Optimal` | Deflate level for the built-in PNG writer where compression pays off (cells ≥ 2 px). 1 px cells always use `Fastest` — their noise-like content is incompressible by construction |
| `PayloadCompressionLevel` | same four values | `Optimal` | Deflate level for compressing the file payload itself |
| `EncodeMemoryBudgetMB` | 64–1000000 | 2000 | Pixel-buffer budget capping parallel encode workers |
| `DecodeMaxParallelism` | 0–1024 | 0 (auto: cores, capped at 16) | Max parallel image decodes |

Deliberately *not* configurable: anything both sides of a transfer must agree on — frame
geometry, metadata-strip layout, magic numbers, Reed-Solomon/GF(2⁸) parameters — plus the
decoder's detection heuristics. Those are protocol, not preference; a settings file on one
machine would silently break decoding on another.

## Capacity and throughput

Per image (with the default ECC): `bytes ≈ grid cells × bits/cell / 8 × 239/255 − ~100`

| Resolution  | Cell | Bits | Payload/image | Capture tolerance |
|------------:|-----:|-----:|--------------:|-------------------|
| 2160²       | 3 px | 4    | ~212 KB       | robust — padding, 1.25-1.5x rescaling, cursors/overlays (default) |
| 2160²       | 2 px | 6    | ~716 KB       | pixel-perfect captures (100% zoom & display scaling) |
| 3840x2160   | 1 px | 6    | ~4.9 MB       | pixel-perfect; fits a 4K display exactly |
| 3840x2160   | 1 px | 8    | ~6.5 MB       | pixel-perfect, ideal conditions |
| 4096²       | 1 px | 8    | ~14.1 MB      | pixel-perfect; needs a >4K display to show at 100% |

**Can you transfer a 300 MB zip? Yes.** At 4K density it is ~65 images; with `-R 10` you also
get 7 parity images so any 7 can be lost. The codec itself is never the bottleneck (about a
second for 300 MB) — end-to-end time is dominated by *capture cadence*: at a manual ~3 s per
screenshot, ~72 images ≈ **3-4 minutes** (~1 MB/s effective); an automated capture loop pushes
that several-fold. At the robust default density the same 300 MB would be ~1,450 images — fine
automated, tedious by hand — which is why dense-on-a-big-display is the right choice for large
files. Hard limits: ≤ 1.5 GB per file (decode is in-memory); display size caps per-image
resolution (the code must be shown at 100% zoom to be pixel-perfect).

## Image formats

Shards can be written in any of six lossless container formats (`-f`); the container is
transport-only — decoding, ECC, and recovery are identical through all of them. Measured on a
100 MB transfer at the default density:

| Format | Encode | Decode | Disk | Notes |
|---|---:|---:|---:|---|
| `png` (default) | 3.0 s | 2.0 s | 365 MB | built-in fast writer; best balance |
| `qoi` | 2.6 s | 2.2 s | 1.5 GB | simplest codec, very fast |
| `bmp` | 4.2 s | 2.5 s | 6.6 GB | uncompressed; disk-write bound |
| `tga` | 3.2 s | 3.5 s | 2.4 GB | RLE |
| `tiff` | 6.2 s | 2.9 s | 973 MB | deflate level 1 |
| `webp` | 21 s | 5.2 s | 194 MB | lossless mode; smallest, slowest |

GIF is deliberately unsupported: its 256-color palette cannot hold the 8-bit cell palette plus
the frame and strip colors. JPEG and other lossy formats are rejected outright — the format
requires bit-exact pixels (though mild JPEG *re-encoding of a capture* is absorbed by ECC).

## Resilience

Four independent layers, from within-image to whole-image:

1. **Reed-Solomon error correction** (`--ecc`, default parity 16): each image's cell stream is
   split into RS codewords whose symbols are interleaved across the image, so localized damage —
   a mouse cursor, a notification toast, mild JPEG re-encoding artifacts — spreads thinly over
   many codewords and is corrected transparently. Default tolerance ≈ 8 damaged bytes per
   255-byte block ≈ a contiguous blob of several thousand cells on a default image.
2. **Cross-shard parity** (`--recovery`, opt-in): extra *parity images* let you lose, delete, or
   fail to capture whole images and rebuild them without recapture. Images are grouped into
   stripes; a stripe of *S* data images gets *P* parity images (a systematic Cauchy Reed-Solomon
   erasure code over GF(2⁸)), and **any** *S* of the *S+P* reconstruct the stripe — there are no
   unlucky loss patterns. This is the layer that matters for large multi-image transfers, where
   finding and recapturing one bad screenshot out of hundreds is the real pain point.
3. **Detection**: a CRC-32 of each image's payload plus a CRC-32-protected header. Damage beyond
   ECC capacity makes an image unreadable — it is then treated as a missing image and recovered by
   layer 2 if parity allows, otherwise reported by exact part number. A SHA-256 of the whole file,
   carried in every image, is verified after reassembly — a successful decode is a cryptographic
   guarantee of a bit-identical file.
4. **Structural redundancy**: the self-describing metadata strip (grid geometry, density, ECC
   level; CRC-16) and the palette calibration strip are both duplicated top and bottom, so an
   overlay across either edge cannot brick an image. The decoder auto-selects the healthier
   palette copy and falls back between metadata copies.

Parity images are self-labelling (`…qrs-parity003of007.png`) and carry the stripe geometry in
every header, so the decoder discovers the recovery layout from any surviving image.

The decoder locates the black locator frame anywhere in the screenshot (multiple ring candidates
are tried until one's metadata validates, so dark desktop surroundings don't confuse it),
measures its inner edge with subpixel precision, and tolerates cropping, padding, and uniform
rescaling. Shards are order-independent, duplicate-tolerant, filename-agnostic, and multiple
files' shards can be mixed in one folder (grouped by random 64-bit file ID).

## Camera capture

Shards encoded with `--camera` also decode from **photos of the screen**, not just screenshots.
The encoder adds four QR-style finder patterns (the classic 7-module 1:1:3:1:1 squares) in
bands above and below the normal layout, plus an orientation tick beside the top-left finder.
Everything inside the frame — metadata, palette strips, data grid, ECC — is unchanged, so
camera-profile shards still decode through the ordinary screenshot path too.

Decoding is automatic, no flag needed. When the axis-aligned pipeline fails, the decoder:

1. detects the finder patterns (run-ratio scan + vertical verification + clustering), resolves
   orientation from the tick (any rotation works, including 90°/180°/270°), and solves the
   four-point homography — this alone handles rotation and perspective;
2. refines for handheld reality using the **black frame itself as a dense alignment
   structure**: its four edges are traced at many points in the photo with subpixel precision,
   and the normal-direction residuals feed a correction field (top/bottom edges loft the
   vertical component, left/right the horizontal — each edge can only observe its own normal)
   that absorbs **lens distortion** (barrel and pincushion) and mild screen curvature;
3. normalizes illumination per pixel: each traced point also samples the frame's black and the
   quiet zone's white, and the interpolated fields flatten **vignette, glare gradients, and
   white-balance shifts** per channel before the color classifier sees anything.

Verified against simulated captures combining rotation, strong perspective (~8% corner
displacement), barrel/pincushion distortion, vignette + lateral glare (brightness varying to
~55%), Gaussian blur, and JPEG re-compression on a non-white background. If refinement cannot
lock onto the frame, the plain homography result is used.

Density is necessarily far lower than screenshots — each cell must span several camera pixels
and only 4 colors are used: **~16 KB per image** at the 4K camera defaults (vs ~4.9 MB for a
screenshot at the same resolution). Use it for documents, keys, and small payloads. Simulated
warps are a good proxy but not a phone: real handheld photos remain the honest acceptance test.

## Benchmark snapshot

Measured on this machine (BenchmarkDotNet means, Monitoring strategy, 3 iterations per case;
decoded output SHA-verified every iteration):

| | |
|---|---|
| CPU | AMD Ryzen 9 9950X3D 16-Core @ 4.3 GHz (family 26, model 68, stepping 0) |
| Cores | 16 physical / 32 logical |
| Motherboard | ASRock X670E Taichi (firmware 4.20) |
| RAM | 4x DDR5-3600, 128 GB total |
| Storage | Crucial T700 2 TB NVMe (temp/work); Corsair MP600 PRO NH 2 TB (artifacts) |
| OS | Windows 11 Pro 25H2 (build 26200.8737) |
| .NET | 10.0.10 (win-x64) |

Presets: **Default** = 2160², 3 px cells, 4 bits (robust); **Dense** = 2160², 2 px, 6 bits;
**Max4K** = 3840x2160, 1 px, 6 bits; **Max4K-R10** = Max4K + 10% parity images.

| Size | Default enc / dec | Dense | Max4K | Max4K-R10 |
|---:|---:|---:|---:|---:|
| 1 KB | 21 / 42 ms | 31 / 54 ms | 112 / 159 ms | 113 / 180 ms |
| 1 MB | 102 / 38 ms | 170 / 62 ms | 137 / 165 ms | 173 / 184 ms |
| 10 MB | 277 / 272 ms | 204 / 95 ms | 217 / 178 ms | 230 / 187 ms |
| 100 MB | 2.68 / 1.72 s | 1.45 / 0.72 s | **0.35 / 0.58 s** | 0.46 / 0.57 s |

The crossover: below ~1 MB every preset needs one image, so the smaller Default canvas wins on
fixed cost; at scale, Max4K packs ~13x more payload per pixel (6 bits/px vs 0.44), so 100 MB is
22 images instead of 495. That dominates end-to-end time too, since every image is a screenshot:

| 100 MB transfer | Images | Est. manual capture (3 s/img) | Est. automated (0.5 s/img) |
|---|---:|---:|---:|
| Default | 495 | 24.8 min | 4.2 min |
| Dense | 147 | 7.4 min | 1.3 min |
| Max4K | 22 | **1.1 min** | **12 s** |
| Max4K-R10 | 22 + 3 parity | 1.3 min | 14 s |

Full interactive charts (log-log codec time, end-to-end estimates, throughput, all numbers) are
generated per run at `tests/QrShard.Benchmarks/BenchmarkDotNet.Artifacts/results/transfer-graphs.html`.

### Running the benchmarks

`tests/QrShard.Benchmarks` is a [BenchmarkDotNet](https://benchmarkdotnet.org/) suite measuring
encode (file → shard images on disk) and decode (images → SHA-verified file) across file sizes
**1 KB – 1 GB** and the four presets, with deterministic incompressible payloads.

```
cd tests/QrShard.Benchmarks
dotnet run -c Release                      # full matrix — ~2 hours, ~5 GB temp disk,
                                           #   ~8 GB free RAM for the 1 GB cases

# Trim either axis without editing code:
QRSHARD_BENCH_SIZES=1KB,1MB,100MB QRSHARD_BENCH_PRESETS=Default,Max4K dotnet run -c Release

dotnet run -c Release -- --graphs-only     # regenerate graphs from persisted results, no runs
```

Results persist in `results/transfer-results.json` and **merge across runs** (latest measurement
of a case wins), so the matrix can be benchmarked in sittings. Output includes the standard
BenchmarkDotNet tables (console/markdown/CSV), the machine-spec header (via WMI on Windows), the
self-contained `transfer-graphs.html`, and `[RPlotExporter]` R plots when
[R](https://www.r-project.org/) is on `PATH`.

## How it works

```
┌──────────────────────────────────────┐
│ white quiet zone                     │
│ ┌──────────────────────────────────┐ │
│ │ solid black locator frame        │ │  ← found automatically in the screenshot
│ │ ┌──────────────────────────────┐ │ │
│ │ │ metadata strip (128 modules) │ │ │  ← geometry + density + ECC level; CRC-16
│ │ │ palette calibration strip    │ │ │  ← decoder classifies vs measured colors
│ │ │                              │ │ │
│ │ │ data grid: W x H cells,      │ │ │  ← RS-protected interleaved bitstream:
│ │ │ 2^bits palette colors        │ │ │    header + payload + RS parity
│ │ │                              │ │ │
│ │ │ palette calibration strip    │ │ │  ← redundant bottom copies
│ │ │ metadata strip (copy)        │ │ │
│ │ └──────────────────────────────┘ │ │
│ └──────────────────────────────────┘ │
└──────────────────────────────────────┘
```

### Codec performance design

- **One flat parallel loop over all images** (data + parity together, no phase barrier), with a
  **thread-local pixel canvas** per worker — reused across images instead of a fresh 15-50 MB
  allocation each. Worker count adapts to the configured pixel-buffer budget.
- **Custom fast PNG writer** ([FastPng.cs](src/QrShard/FastPng.cs), ~150 lines) for the default
  format: fixed None/Up filter, zlib-ng, one IDAT streamed straight from the render buffer with
  an incrementally-computed chunk CRC. Standard PNG output, verified pixel-identical through
  ImageSharp. Non-PNG formats go through ImageSharp with lossless speed-tuned settings and a
  dedicated pooled memory allocator.
- **Streaming encoder**: incompressible inputs (the common big-transfer case) are memory-mapped
  and read per-chunk by the parallel workers — the file is never materialized as a managed array.
- **Table-driven Reed-Solomon** (precomputed generator/α-power tables) plus a **SIMD syndrome
  scan**: the FEC buffer is symbol-interleaved, so 16 codewords' syndromes compute together —
  one `Vector128` lane per codeword — and clean codewords skip the scalar decoder entirely.
  Cross-shard parity uses nibble-shuffle SIMD GF(2⁸) multiply-accumulate.
- **GC discipline**: server GC; per-worker scratch buffers on both encode and decode (pixels,
  flood-fill map, staging/recovery buffers, color LUT); exact-size reassembly and decompression
  buffers; no nested parallelism. A 100 MB decode allocates ~0.9 GB total, down from ~9.5 GB in
  the first implementation.

### Image library choice

Decode must parse arbitrary screenshots from unknown tools — that needs a mature library:
**ImageSharp** (pure managed, cross-platform; v4, used under a Six Labors community license —
see below). Encode owns every pixel and needs no library, hence FastPng.
SkiaSharp (native dependency, no TGA/QOI encode), Magick.NET (heavyweight), System.Drawing
(Windows-only, deprecated), and Stb ports (too weak on both sides) were evaluated and rejected.
A fully custom *container* format was rejected too: shards must be displayable by ordinary OS
image viewers on the sending machine.

## Capture tips

- Display the image at **100% zoom** and screenshot it (a cropped region capture is fine — just
  include the whole black frame with a little margin).
- `-r auto` sizes shards to your monitor; on a 4K display add `-c 1 -b 6` for maximum density.
- For cell sizes below 3 px the capture must be pixel-perfect: avoid fractional display scaling
  (125%/150%) and browser zoom.
- Cursors, small overlays, and high-quality JPEG re-encoding are absorbed by ECC; raise
  `--ecc` (e.g. 32) for hostile conditions, or lower it toward 0 for maximum capacity.
- Rotation/perspective is only supported for `--camera` shards (see
  [Camera capture](#camera-capture)); the default screenshot profile assumes an
  axis-aligned capture.

## Building and testing

Requires the .NET 10 SDK. `dotnet build -c Release` at the solution root; `./publish.ps1` for
standalone binaries.

ImageSharp 4.x validates a license at build time. License keys are personal and **not
committed** to this repository: to build, obtain your own (free community licenses for
qualifying open-source use at https://sixlabors.com/pricing/) and either drop `sixlabors.lic`
at the solution root (gitignored; `Directory.Build.props` picks it up for every project) or set
the `SixLaborsLicenseKey` environment variable. The license is build-time only; published
binaries and end users need nothing.

- `dotnet test` — 310 xUnit tests, ~2 s, **~93% line coverage** (the uncovered remainder is
  mostly per-platform display-detection code that can't run on a single OS)
  (`dotnet test --collect:"XPlat Code Coverage"` to reproduce). Covers: CRC check-vectors,
  GF(2⁸) field laws and matrix inversion, Reed-Solomon (max-error correction, beyond-capacity
  detection, shortened codewords), FEC interleaving (burst/scatter damage, SIMD blocks + tails),
  cross-shard erasure coding (exhaustive loss-pattern MDS verification), palette
  construction/classification, bit packing, layout geometry, metadata strip
  (every-single-bit-flip rejection), header robustness (corruption/truncation/unicode/parity),
  round trips across density/ECC/format configs, FastPng pixel identity, streaming source
  behavior, simulated captures (padded/rescaled/cropped/dark-background/JPEG), damage and
  whole-image recovery, shard-set handling, settings parsing/validation, xrandr parsing, and
  the CLI including settings-vs-flags precedence.
- `qrshard test` — end-to-end self-test at real resolutions (up to a 14 MB single image),
  including simulated screenshots with cursor damage and a cross-shard recovery scenario.
