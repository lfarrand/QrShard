# QrShard

[![CI](https://github.com/lfarrand/QrShard/actions/workflows/ci.yml/badge.svg)](https://github.com/lfarrand/QrShard/actions/workflows/ci.yml)

QrShard transfers files between machines through the screen: it encodes any file (or folder)
into a series of high-density, QR-style images which are displayed on one machine, captured on
another — by screenshot, phone photo, screen recording, or a live webcam — and reconstituted
back into the original file, **bit-for-bit, verified by SHA-256**.

The image format is custom (not QR-standard) and tuned for screen-to-screenshot transfer.
Because a screenshot is a lossless pixel copy, each image can be vastly denser than a real QR
code: from ~212 KB per image at the robust default up to **~6.5 MB per image** on a 4K display —
so a 100 MB file fits in 22 screenshots and a 300 MB zip in ~65. Layered error correction
(including errors-and-erasures Reed-Solomon fed by the classifier's own confidence) absorbs
cursors, pop-ups, and re-encoding; parity or fountain-coded images let whole captures be lost
and rebuilt; multiple failed photos of the same image can be *fused* into a good one; payloads
can be AES-256-GCM encrypted end to end.

**Contents:** [Platforms](#supported-platforms) · [Install](#installing) ·
[How to use](#how-to-use-it) · [Options](#commands-and-options) ·
[Workflow tools](#workflow-tools-sessions-watch-verify-heatmap-calibrate) ·
[Configuration](#configuration-appsettingsjson) · [Capacity](#capacity-and-throughput) ·
[Sample output](#sample-output) · [Formats](#image-formats) · [Resilience](#resilience) ·
[Camera capture](#camera-capture) ·
[Benchmarks](#benchmark-snapshot) · [Design notes](#how-it-works) ·
[Building & testing](#building-and-testing) · [Security](SECURITY.md)

## Supported platforms

The codec is pure managed .NET 10 — no native dependencies — and the wire format is
platform-agnostic by construction, so shards encoded on one OS decode on any other. That is not
left to construction alone: the **Interop** workflow runs all sixteen pairs of
{win-x64, linux-x64, linux-arm64, osx-arm64} encoders and decoders on every pull request, and
each pair deletes a data image so the decode has to rebuild it through Cauchy parity — the
GF(2⁸) kernels take a GFNI/AVX route on x64 and a different one on arm64, and a disagreement
there would produce wrong bytes rather than a loud failure. The encrypted path is covered too,
which also pins that the AES-GCM associated data is assembled identically on both sides.

| Platform | Codec | Monitor auto-detection (`-r auto`) | Benchmark machine spec |
|---|---|---|---|
| Windows (x64) | ✅ | ✅ EnumDisplaySettings (physical pixels, DPI-scaling-proof) | ✅ WMI |
| Linux (x64/arm64) | ✅ verified via WSL | ✅ `xrandr` parsing (X11/XWayland); headless falls back | degraded (OS + .NET + cores) |
| macOS (x64/arm64) | ✅ (managed-only code) | ✅ CoreGraphics Retina pixel dimensions (untested on real hardware) | degraded |

Video decoding and the live receiver additionally need [ffmpeg](https://ffmpeg.org) on `PATH`
(animated png/gif/webp recordings decode natively without it).

## Installing

- **dotnet tool**: `dotnet tool install -g QrShard.Tool` → the `qrshard` command (needs the
  .NET 10 runtime).
- **Standalone binaries**: tagged releases attach Native-AOT single-file binaries for
  win-x64 / linux-x64 / linux-arm64 / osx-arm64 — no .NET install needed. `./publish.ps1`
  (or `.sh`) produces the same locally.
- **As a library**: `dotnet add package QrShard.Core` — the embeddable codec, wire-compatible with
  the CLI. `QrShardCodec.EncodeFile` / `DecodeImages` for one-shot use, plus `QrShardDecodeSession`
  for **incremental** decoding: feed captures (files or in-memory image bytes) as they arrive,
  query which images are still missing, and assemble the moment the set is recoverable.
- **From source**: `dotnet run --project src/QrShard -c Release -- <command>` (see
  [Building](#building-and-testing) for the ImageSharp license note).

Both packages are published to [nuget.org](https://www.nuget.org/packages/QrShard.Tool) by the
release workflow, using trusted publishing — no API key is stored in this repository. Releases are
also mirrored to GitHub Packages so they appear under this repository's **Packages** section, but
**nuget.org is the supported install source**: GitHub Packages requires an access token with
`read:packages` to install from, even for public repositories.

Shell completions for bash and PowerShell live in [`completions/`](completions/). The wire
format is fully specified in [SPEC.md](SPEC.md) — an independent implementation can be built
from it.

## How to use it

**On the sending machine:**

```
qrshard encode holiday-photos.zip            # a folder works too — tar-ed/extracted automatically
qrshard encode secrets.db -p "correct horse" # AES-256-GCM encrypted payload
```

This creates `holiday-photos.zip.shards/` next to the input, containing numbered images sized to
your primary monitor. Open the folder in any image viewer, display each image fullscreen at
**100% zoom**, and capture each one for the receiving side (a cropped region capture is fine —
just include the whole black frame with a little margin). For large files add `-R 10` so up to
~10% of the captures can be botched or skipped without redoing anything.

**On the receiving machine**, put the captures in a folder (any filenames, any order,
duplicates fine) and:

```
qrshard decode captures\ -o holiday-photos.zip
```

Every image is CRC-verified as it's read; damaged captures are repaired by error correction,
fused from multiple failed photos, or rebuilt from parity images; anything unrecoverable is
reported by exact part number ("missing image 7 of 22 — recapture it"); and the final file is
verified against a SHA-256 carried inside the shards. If decode says it succeeded, the file is
bit-identical.

**Video mode — no manual capturing at all.** Add `--video` when encoding and a self-contained
`slideshow.html` is written next to the shards: open it in any browser, press F11, and it
cycles every image forever (default 500 ms each; `--interval` to tune; `--slideshow apng` writes a
single animated PNG instead of an HTML page, for setups where one media file is easier to display
or record). On the receiving side, **record the screen** for one full cycle — or point a phone at
it (`--camera` shards decode from handheld video, with the detected pose cached between frames) —
and feed the recording in:

```
qrshard decode recording.mp4 -o holiday-photos.zip
```

Near-duplicate frames are skipped cheaply, torn mid-transition frames fail checksums harmlessly
and come around again next cycle, and decoding **stops early** the moment the collected set is
complete or recoverable. If a file recording still comes up short, it is automatically re-extracted
at a higher frame rate before giving up. Add `-F 100` (fountain coding) when encoding and the slideshow also
cycles random-linear coded frames: **any** enough captured frames per stripe reconstruct the
data, so lost or glared frames simply don't count — the ideal mode for lossy capture chains.

**Live mode — no recording either.** Point a webcam (or capture card) at the sender's screen:

```
qrshard receive --device "Integrated Camera"
```

frames stream through ffmpeg and decode in real time; the capture stops itself the moment the
transfer completes.

`--screen` reads *this* machine's display instead of a camera, which is how you get a file **out**
of a locked-down remote: run the slideshow inside the RDP or VM window and decode the host's own
screen. Narrow it to just that window with `--region x,y,w,h` (e.g. `--region 100,80,1920,1080`)
so the rest of the desktop is never captured or scanned.

## Commands and options

| Command | Description |
|---|---|
| `qrshard encode <file\|folder> [options]` | Split a file (or tar-ed folder) into shard images |
| `qrshard send <file\|folder> [options]` | Encode + open the slideshow in the default browser |
| `qrshard decode <folder\|images...\|recording> [options]` | Reconstitute the original from captures or a recording (`--watch` to keep decoding as captures land; `--clipboard` on Windows; `--json` for scripts) |
| `qrshard receive [--device d \| --screen] [options]` | Live decode from a webcam — or from THIS machine's screen (`--screen`): put the slideshow in an RDP/VM window and transfer out of locked-down remotes |
| `qrshard verify <folder\|images...> [--session f] [--json]` | Report set completeness without writing output |
| `qrshard info <image> [--heatmap out.png] [--quality-heatmap out.png] [--json]` | Inspect/validate one shard; render an ECC damage map or a capture-quality map (works even on a *failed* capture) |
| `qrshard calibrate [-o dir] [--camera] / calibrate <folder>` | Probe → capture → recommended density settings |
| `qrshard test [<file> [encode opts]]` | Built-in self-test, or round-trip *your* file at *your* settings through simulated screenshots and report the ECC headroom it used |
| `qrshard --version` | Print the version (also `-v` / `version`) — the same version the package and release binaries carry |
| `qrshard --help` | Show usage (also `-h` / `help`) |

### `encode` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `-o, --out <dir>` | any path | `<file>.shards` next to the input | Output folder for the shard images |
| `-r, --resolution <px>` | `auto`; one number (square); `WxH` — 700–16384 per side | `auto` | Image size. `auto` detects the primary monitor's native resolution so shards fill the screen they'll be captured from |
| `-c, --cell <px>` | 1–64 | 3 | Data cell size in pixels. 3 survives fractional display rescaling; 1 doubles-to-quadruples density but needs pixel-perfect captures |
| `-b, --bits <n>` | 1–8 | 4 | Bits per cell (color density): 2ⁿ palette colors |
| `-e, --ecc <n>` | even, 0–64 | 16 | Reed-Solomon parity bytes per 255-byte block. 16 ≈ 6% overhead; fixes 8 unknown-position bytes/block, up to ~14 when the classifier can flag them (erasures) |
| `-R, --recovery <pct>` | 0–100 | 0 (off) | Extra **parity images** (Cauchy erasure code): any lost images up to the budget are rebuilt without recapture |
| `-F, --fountain <pct>` | 0–1000 | 0 (off) | **Fountain-coded frames** (random linear code) for video mode: any enough captured frames per stripe reconstruct the data; no per-stripe frame-count ceiling. Mutually exclusive with `-R` |
| `-p, --password <pw>` | any string | off | AES-256-GCM encrypt the payload (PBKDF2-SHA256 key); decode needs the same password |
| `-f, --format <fmt>` | `png`, `bmp`, `tga`, `qoi`, `webp`, `tiff` | `png` | Lossless container format |
| `--camera` | flag | off | Camera profile: finder patterns so shards decode from **photos/handheld video** of the screen; shifts defaults to cell 8 / 2 bits / ECC 32 |
| `--video` | flag | off | Also write a slideshow (see `--slideshow`) for recording-based capture |
| `--slideshow <kind>` | `html`, `apng` | `html` | With `--video`: a self-contained `slideshow.html` page, or a single animated PNG (`slideshow.apng`) cycling the shards — useful where one media file is easier to display/record than a browser page |
| `--open` | flag | off | With `--video`: open the slideshow in the default browser once encoding finishes. `qrshard send` is exactly `encode --video --open` |
| `-i, --interval <ms>` | ≥ 100 | 500 | Slideshow interval per image (both slideshow kinds) |
| `--interleave2` | flag | off | v2 permuted interleave: spreads **vertical** damage (a horizontal banner/overlay) across codewords as well as horizontal. Needs ECC; rides a metadata-version nibble so older decoders reject it rather than misread |
| `--profile <name>` | a name in `appsettings.json` `EncodeProfiles` | — | Apply a named encode preset (see [Configuration](#configuration-appsettingsjson)); explicit flags still override it |
| `--json` | flag | off | Emit the encode result (image/parity counts, geometry, file list, slideshow path) as JSON on stdout instead of the human summary |
| `--dry-run` | flag | off | Print the exact image count and geometry — computed after compression, without rendering — then exit. A guardrail before a large folder silently emits hundreds of PNGs. Honors `--json` |
| `--no-compress` | flag | compression on | Skip Brotli compression of the payload (auto-skipped when a sample shows the file is incompressible) |

**Multiple inputs** are bundled into one archive and extracted on decode:
`qrshard encode report.pdf photos/ notes.txt -o release.shards`. A single folder flattens to the
archive root; multiple inputs keep their names (colliding names are refused, never silently
overwritten). Unknown or misspelled options are rejected up front — a typo'd `--pasword` errors
with a "did you mean" hint rather than silently encoding **unencrypted**.

### `decode` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `-o, --out <path>` | any path | original filename in the current directory (never overwrites — falls back to `<name>.restored<ext>`, then `.restored-2`, `.restored-3`, …) | Where to write the file (a directory for archive payloads) |
| `-p, --password <pw>` | any string | — | Password for encrypted payloads (clear error if missing or wrong) |
| `--session <file>` | any path | off | Accumulate shards across sittings: incomplete sets persist (exit 3) with a missing-image report; the next run resumes from the union; deleted on success. Applies to decoding **images**; a recording is re-read from the start each time, so passing it there is rejected rather than silently ignored |
| `--watch` | flag | off | Keep watching the folder: decode captures as they land, assemble the moment the set completes; Ctrl+C persists to the session |
| `--clipboard` | flag | off | (Windows) decode the bitmap on the clipboard — snip a displayed shard with Win+Shift+S, no file saving; accumulates with `--session` |
| `--fps <n>` | > 0 | 8 | Frame extraction rate when decoding a video recording. If not pinned, an incomplete file recording is automatically re-extracted at 2× then 4× until the set completes |
| `--json` | flag | off | Emit the result as JSON on stdout instead of the human log — the restored files with their **resolved output paths** and lengths, or the per-file completeness status when the set is incomplete |

A plain `decode` of an incomplete folder prints the same per-file status `verify` shows, names
the missing images, points you at `--session`/`--watch`, and exits **3** (distinct from a hard
error) — nothing already collected is lost.

### Scripting: exit codes and `--json`

| Exit code | Meaning |
|---|---|
| `0` | Success |
| `2` | Usage error (unknown command, missing argument, misspelled option) — usage goes to stdout, the reason to stderr |
| `3` | Valid but **incomplete**: images are missing. Capture more and run again; nothing collected is lost |
| `1` | Anything else — unusable images, corruption, a wrong password, I/O |

`encode`, `decode`, `verify` and `info` take `--json`; every human progress line is suppressed so
stdout is nothing but the JSON document (errors still go to stderr). A complete `decode` reports
what it wrote — the path matters because without `-o` the destination comes from the shard header
and takes a `.restored` fallback if the name is already taken:

```console
$ qrshard decode captures/ --json
{
  "complete": true,
  "restored": [
    { "fileName": "report.pdf", "outputPath": "/home/me/report.restored.pdf", "length": 3145728 }
  ]
}
```

An incomplete `decode` (exit 3) and `verify` share one shape, so a script reads either the same
way — `.complete`, then `.files[].missing`:

```console
$ qrshard decode captures/ --json; echo "exit $?"
{
  "complete": false,
  "files": [
    { "fileName": "report.pdf", "fileId": "aca8f0d2a53524aa", "dataPresent": 24,
      "dataTotal": 26, "parityPresent": 0, "recoverable": false, "missing": [3, 11] }
  ]
}
exit 3
```

There is deliberately no `restored` key on that shape: a folder mixing one complete file with one
incomplete file writes the complete one before stopping, and which files landed is not tracked on
that path — so listing them would be a guess.

## Workflow tools: sessions, watch, verify, heatmap, calibrate

- **Sessions** (`--session s`): capture in as many sittings as you like; every decoded shard
  persists to a CRC-guarded session file and each run reports exactly what's still missing.
- **Watch mode** (`decode incoming/ --watch --session s`): leave the receiver running and just
  keep dropping captures in — it decodes each as it lands and assembles automatically.
- **`verify`**: is this set complete/recoverable? Per-file data/parity counts, missing indices,
  parity-coverage status; exit 0 only when fully reassemblable, 3 when images are still missing
  (1 is reserved for images that are unusable — the one answer capturing more cannot fix).
  `--json` for scripts.
- **`info --heatmap out.png`**: a per-cell ECC damage map — green (clean) through red (heavily
  corrected) to dark red (beyond correction) — showing exactly where the glare blob or cursor
  landed. When a capture fails so badly there is no correction data to map, it falls back to the
  quality map below.
- **`info --quality-heatmap out.png`**: a capture-**quality** map from each cell's classification
  confidence (how cleanly it matched a palette color). Unlike the ECC map it renders even for a
  capture that never decoded at all — so you can see *where* focus/glare/rescaling hurt a totally
  failed shot and fix the capture.
- **`test <file> [encode opts]`**: encode *your* file at *your* settings, run it through the same
  simulated screenshot degradation the self-test uses, and report whether it survives and the
  worst-case ECC headroom it consumed — the "will my file at these settings make it?" check the
  fixed-fixture self-test can't answer. (`test` alone still runs the built-in self-test.)
- **`calibrate`**: writes a ladder of self-describing density probes; capture them exactly like
  a real transfer and `qrshard calibrate <capturedFolder>` measures what survived, recommending
  the densest `-c/-b` that decoded with comfortable ECC headroom on *your* screen/capture pair.

## Configuration (appsettings.json)

An optional `appsettings.json` next to the executable holds preferences and machine tuning.
Comments are allowed in it and every value is documented inline there. Precedence: **CLI flag >
appsettings.json > built-in default**. Invalid values fail loudly, naming the setting.

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
| `PngCompressionLevel` | `Optimal`, `Fastest`, `SmallestSize`, `NoCompression` | `Optimal` | Deflate level for the built-in PNG writer where compression pays off (cells ≥ 2 px). 1 px cells bypass deflate entirely (stored blocks — their noise-like content is incompressible by construction) |
| `PayloadCompressionLevel` | same four values | `Optimal` | Brotli level for compressing the file payload |
| `EncodeMemoryBudgetMB` | 64–1000000 | 2000 | Pixel-buffer budget capping parallel encode workers. Encode workers are additionally hard-capped at the logical core count, so raising this past what the cores can use changes nothing |
| `DecodeMaxParallelism` | 0–1024 | 0 (auto: cores, capped at 24) | Max parallel image decodes. The cap trades throughput for memory rather than marking a plateau — at 4K, 24 workers decode ~20% faster than 16 for ~1.5 GB more peak working set, and 32 adds a further ~8% for ~0.45 GB. Raise it if you have memory to spare; lower it on memory-constrained machines |
| `DecodeMemoryBudgetMB` | 64–1000000 | 4000 | Scratch budget for parallel decoding — the counterpart to `EncodeMemoryBudgetMB`. Workers are the lower of `DecodeMaxParallelism` and what this affords against the largest image in the set (read from its header first). A 4K frame costs ~33 MB of scratch and a 48 MP photo ~192 MB, so the default admits the full worker count for any realistic capture and binds only on far larger images |
| `ReceiveFps` | 0–120 | 10 | Default frame rate for the live `receive` capture |
| `WatchPollMs` | 50–60000 | 250 | Folder poll interval (ms) for `decode --watch` |
| `ReceiveDecodeWorkers` | 0–64 | 0 (auto) | Parallel frame-decode workers for the live receiver |
| `EncodeProfiles` | `{ "<name>": { …encode-default keys… } }` | (none) | Named encode presets selected with `--profile <name>`; each starts from `EncodeDefaults` and overrides only the keys it names |

### Tuning for a large machine

**The memory budgets only ever lower the worker count; neither can raise it.** Both are
`Clamp(budget / per-worker-bytes, 1, someCap)`, so a bigger budget removes a constraint that may
not have been binding in the first place. On a machine with plenty of RAM the budgets are almost
never what limits you — the worker caps are:

| | Worker cap | Raisable? |
|---|---|---|
| **Encode** | the logical core count | **No.** No setting exceeds it — more workers than cores does not help CPU-bound work |
| **Decode** | `DecodeMaxParallelism`, default auto = `min(cores, 24)` | **Yes**, up to 1024 |

So on a 32-thread machine the encoder already uses all 32 whenever memory allows, while the
decoder stops at 24 and leaves 8 threads idle. The knob that helps is the parallelism cap, not
the budget:

```json
{ "DecodeMaxParallelism": 32 }
```

Worth about **+8.4% median decode throughput** at 4K on a 16-core/32-thread part (measured idle,
24 vs 32 workers) for roughly 0.45 GB more peak working set.

Raising `DecodeMemoryBudgetMB` only matters if you decode images far larger than a camera
produces — at the 500-megapixel per-image ceiling a worker holds ~2 GB, so a full pool would need
tens of GB. That is a reasonable setting for very high-resolution scans **you produced yourself**.
Be aware it also removes the bound for a *hostile* set: the image dimensions come from whoever
made the shards, and that ceiling is what the budget exists to enforce.

Deliberately *not* configurable: anything both sides of a transfer must agree on — frame
geometry, metadata-strip layout, magic numbers, Reed-Solomon/GF(2⁸) parameters — plus the
decoder's detection heuristics. Those are protocol, not preference. Shards carrying header
flags from a newer QrShard fail with an explicit "update QrShard" error rather than decoding
wrong.

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
that several-fold. Not sure what density your setup survives? `qrshard calibrate`. Hard limits:
≤ 1.5 GB per file; display size caps per-image resolution.

### Decode frame rate

How fast the *receiver* turns captured images back into data — the number that decides whether
the decoder can keep up with a slideshow or a video. Two figures per density: **one core** (the
portable per-frame cost) and **parallel** (the default decoder, up to 24 image workers). Payload
rate is the parallel frame rate times the payload each frame carries; the 250 MB column is decode
time alone at that rate.

| Resolution / density | Payload / image | Frames/s, 1 core | Frames/s, parallel* | Payload rate* | 250 MB, decode only* |
|---|---:|---:|---:|---:|---:|
| 2160² · 3 px · 4-bit *(Default)* | ~212 KB | ~52 (19 ms) | ~238 | ~50 MB/s | ~5.0 s |
| 2160² · 2 px · 6-bit *(Dense)* | ~716 KB | ~39 (25 ms) | ~218 | ~156 MB/s | ~1.6 s |
| 3840×2160 · 1 px · 6-bit *(Max4K)* | ~4.9 MB | ~14 (70 ms) | ~81 | ~397 MB/s | ~0.6 s |
| 3840×2160 · 1 px · 8-bit | ~6.5 MB | ~13 (77 ms) | ~76 | ~498 MB/s | ~0.5 s |

\*Parallel figures are on the [benchmark machine](#benchmark-snapshot) (32 logical cores) and
scale down on fewer cores; the one-core column does not. The worker cap is a **memory** ceiling,
not a bandwidth one — each worker holds a full-resolution pixel buffer, while PNG read is only
~6.5% of a 4K image's decode. Raise `DecodeMaxParallelism` if you have memory to spare. Work is
handed out one image at a time rather than in pre-assigned ranges, so the rate does not depend on
whether the image count divides evenly by the worker count. Reproduce with `dotnet run -c Release
-- --fps-probe` in `tests/QrShard.Benchmarks`.

Notice the frame rate *falls* with density while the payload rate *rises*: a 4K frame decodes
about 4x slower than a 2160² one but carries ~23x more data, so denser images move more bytes
per second and a fixed transfer needs fewer of them — the same reason Max4K wins the
[transfer charts](#charts). It also means **decode is never the limit in practice**: the default
slideshow runs at 2 frames/second (500 ms/image), and even the slowest one-core rate here (~13
fps at 4K) clears that ~6x over, while every parallel rate clears a 60 Hz display. The parallel
250 MB decode times — well under a second at 4K — sit far below the capture-bound reality of the
same transfer: the benchmark table puts 250 MB at Max4K around **28 s** even with a scripted
0.5 s/image loop, and minutes by hand. The decoder spends almost all of a real transfer waiting
for the next frame.

## Sample output

Real, unmodified encoder output. Each row is one shard image: the left view is the **whole
image** scaled down to fit the page, the right view is an exact **150 x 150 pixel region
magnified 3x** with no resampling, so you can see the individual cells at their true relative
size. The payload is random bytes sized to fill the grid — that is why the data field looks like
noise and why there is no blank space.

| Configuration | Whole image (scaled) | Cells at 1:1 (150 px region, 3x) |
|---|---|---|
| **Default** — `-c 3 -b 4`<br>2159 x 2159 px · 3 px cells · 16 colours<br>~212 KB per image | <img src="docs/samples/default-full.png" alt="Default preset shard: white quiet zone, black frame, metadata and palette strips top and bottom, dense multicoloured data field" width="380"> | <img src="docs/samples/default-detail.png" alt="Default preset cells magnified: 3-pixel square cells in 16 colours" width="380"> |
| **Dense** — `-c 2 -b 6`<br>2160 x 2160 px · 2 px cells · 64 colours<br>~716 KB per image | <img src="docs/samples/dense-full.png" alt="Dense preset shard at 2 pixel cells and 64 colours" width="380"> | <img src="docs/samples/dense-detail.png" alt="Dense preset cells magnified: 2-pixel cells in 64 colours" width="380"> |
| **Max4K** — `-r 3840x2160 -c 1 -b 6`<br>3840 x 2160 px · 1 px cells · 64 colours<br>~4.9 MB per image | <img src="docs/samples/max4k-full.png" alt="Max4K preset shard filling a 4K display at one pixel per cell" width="380"> | <img src="docs/samples/max4k-detail.png" alt="Max4K cells magnified: one pixel per cell in 64 colours" width="380"> |
| **Camera** — `--camera`<br>3836 x 2160 px · 8 px cells · 4 colours<br>~16 KB per image | <img src="docs/samples/camera-full.png" alt="Camera profile shard with four QR-style finder patterns in bands above and below the data area" width="380"> | <img src="docs/samples/camera-detail.png" alt="Camera profile cells magnified: 8-pixel cells in 4 colours" width="380"> |
| **Monochrome** — `-c 8 -b 1`<br>2154 x 2158 px · 8 px cells · black/white<br>~7 KB per image | <img src="docs/samples/mono-full.png" alt="Monochrome shard: 8 pixel black and white cells" width="380"> | <img src="docs/samples/mono-detail.png" alt="Monochrome cells magnified: 8-pixel black and white cells, QR-like" width="380"> |

What you are looking at, from the outside in: a white **quiet zone**, the black **frame** the
decoder traces to find and rectify the image, then the **metadata strip** (the black/white run
carrying geometry and part numbers) and the **palette calibration strip** (the coloured swatches
the classifier calibrates against) — both duplicated top *and* bottom so an overlay across either
edge cannot brick the image — and finally the data field itself.

The `--camera` row is the odd one out: it adds four QR-style **finder patterns** in bands above
and below the data, plus the small orientation tick beside the top-left finder, which is what
lets a photo or handheld video be located and de-skewed. It pays for that in density — 4 colours
at 8 px cells is ~16 KB per image against Max4K's ~4.9 MB, roughly 300x less.

The monochrome row is the opposite extreme and the one that looks most like a conventional QR
code. It is not a preset, just `-c 8 -b 1`; every setting in between is available.

> The whole-image views are scaled down for the page and **will not decode** — a real capture has
> to be pixel-accurate. The 1:1 detail crops are true pixels, but only a fragment of a shard.

Regenerate them with [`docs/samples/regenerate.ps1`](docs/samples/regenerate.ps1). Only these
derived views are committed: a filled Max4K shard is ~25 MB of essentially incompressible pixels
at full resolution. The payload comes from a fixed seed, so the data field is stable between
runs — but the images are not byte-identical, because every encode stamps a random 64-bit file
id (that is what lets shards of *different* files share a folder without being confused), and
that id lives in the metadata strip.

## Image formats

Shards can be written in any of six lossless container formats (`-f`); the container is
transport-only — decoding, ECC, and recovery are identical through all of them. Measured on a
100 MB transfer at the default density:

| Format | Encode | Decode | Disk | Notes |
|---|---:|---:|---:|---|
| `png` (default) | 3.0 s | 2.0 s | 365 MB | built-in fast writer *and reader*; best balance |
| `qoi` | 2.6 s | 2.2 s | 1.5 GB | simplest codec, very fast |
| `bmp` | 4.2 s | 2.5 s | 6.6 GB | uncompressed; disk-write bound |
| `tga` | 3.2 s | 3.5 s | 2.4 GB | RLE |
| `tiff` | 6.2 s | 2.9 s | 973 MB | deflate level 1 |
| `webp` | 21 s | 5.2 s | 194 MB | lossless mode; smallest, slowest |

GIF is deliberately unsupported: its 256-color palette cannot hold the 8-bit cell palette plus
the frame and strip colors. JPEG and other lossy formats are rejected outright — the format
requires bit-exact pixels (though mild JPEG *re-encoding of a capture* is absorbed by ECC).

## Resilience

Six independent layers, from within-cell to whole-transfer:

1. **Reed-Solomon error correction** (`--ecc`, default parity 16): each image's cell stream is
   split into RS codewords whose symbols are interleaved across the image, so localized damage —
   a mouse cursor, a notification toast, mild JPEG re-encoding artifacts — spreads thinly over
   many codewords and is corrected transparently.
2. **Errors-and-erasures decoding**: the color classifier flags cells whose classification was
   ambiguous (far from every palette color, or nearly a tie), and codewords that fail
   errors-only decoding retry with those flags as *erasures* — RS corrects twice as many known
   positions as unknown ones (`2·errors + erasures ≤ parity − 2`), so borderline captures gain up
   to ~75% more correctable damage per codeword.

   Two syndromes are always held back from that budget, so at the default parity of 16 up to 14
   flagged bytes per block are usable. That reserve is what makes the result *checkable*: an
   erasure decode that spends every syndrome is exactly determined, so it always yields some
   valid codeword and the verification step passes even when the answer is wrong. A codeword
   flagged beyond the budget is handed to Chase decoding instead of being answered on a guess.
   Wrong flags on a healthy codeword still cost nothing.
3. **Multi-capture fusion**: several photos of the same shard that each fail on their own are
   combined — per-codeword selection with a majority vote from three captures up; with exactly
   two, the spatial clusters where the captures disagree are hypothesis-tested (glare moves
   between shots; the payload CRC gates the answer).
4. **Cross-shard parity** (`--recovery`) or **fountain coding** (`--fountain`): whole missing
   images are rebuilt without recapture. Parity is a systematic Cauchy erasure code — any *S*
   of the stripe's *S+P* images reconstruct it. Fountain frames are random linear combinations
   with header-derivable coefficients — any full-rank subset of captured frames solves the
   stripe, with no ceiling on how many distinct frames the sender can cycle.
5. **Detection**: CRC-32 per payload, CRC-32-protected headers, and a SHA-256 of the whole file
   carried in every image and verified after reassembly — a successful decode is a
   cryptographic guarantee of a bit-identical file. Encrypted payloads are additionally
   authenticated by AES-GCM, which also **binds the cleartext identity fields** (original size,
   SHA-256, filename) as associated data: because the header CRC is an integrity check, not a MAC,
   an attacker could recompute it — but a tampered filename/size on an encrypted shard now fails
   decryption up front rather than silently mis-routing a write.
6. **Structural redundancy**: the self-describing metadata strip and the palette calibration
   strip are duplicated top and bottom, so an overlay across either edge cannot brick an image.
   When both palette strips are healthy but differ (vertical illumination gradients — screen
   falloff, room light), the decoder *interpolates the reference palette per grid row* between
   them instead of picking one.

Parity/fountain images are self-labelling and carry the stripe geometry in every header, so the
decoder discovers the recovery layout from any surviving image. Shards are order-independent,
duplicate-tolerant, filename-agnostic, and multiple files' shards can be mixed in one folder.

## Camera capture

Shards encoded with `--camera` also decode from **photos of the screen** — and from **handheld
video** of the slideshow. The encoder adds four QR-style finder patterns in bands above and
below the normal layout, plus an orientation tick. Decoding is automatic: when the axis-aligned
pipeline fails, the decoder detects the finders (any rotation, including 90°/180°/270°), solves
the four-point homography, then refines for handheld reality using the **black frame itself as
a dense alignment structure** — traced-edge residuals feed a correction field absorbing lens
distortion and screen curvature, and per-point black/white samples flatten vignette, glare
gradients, and white-balance shifts before the color classifier sees anything.

Finder detection runs on a **Sauvola local-contrast binarization** (a per-window threshold driven
by both local mean and local variance), which holds up under the uneven illumination — screen
falloff, glare washout — that defeats a single global threshold. When exactly three of the four
finders survive (a finger or glare over one corner), the fourth is **reconstructed** by
parallelogram completion, so a partially-occluded capture that used to be discarded still decodes
(the payload CRC gates any bad reconstruction).

For video, the detected pose is **cached across frames**: consecutive handheld frames share
nearly the same pose and the refinement absorbs the drift, so full finder detection only reruns
when a cached pose stops decoding. A capture-mode latch keeps plain screen recordings from ever
paying for camera detection, and a cheap **sharpness gate** skips hopelessly motion-blurred frames
before the expensive rectification. When no single frame of a group decodes, the group's
near-duplicate frames are **averaged** and retried — independent sensor noise averages down, which
can push a marginal blurred shard back over the error-correction threshold.

Density is necessarily far lower than screenshots (~16 KB per image at the 4K camera defaults):
use it for documents, keys, and small payloads. Simulated warps (rotation, ~8% perspective,
barrel/pincushion, vignette + glare to ~55% brightness, blur, JPEG) are a good proxy, but real
handheld photos remain the honest acceptance test.

## Benchmark snapshot

Measured on this machine (BenchmarkDotNet means, Monitoring strategy, 3 iterations per case;
decoded output SHA-verified every iteration):

| | |
|---|---|
| CPU | AMD Ryzen 9 9950X3D 16-Core @ 4.3 GHz (family 26, model 68, stepping 0) |
| Cores | 16 physical / 32 logical |
| Motherboard | ASRock X670E Taichi (firmware 4.43) |
| RAM | 4x DDR5-3600, 128 GB total |
| Storage | Crucial T700 2 TB NVMe (temp/work); Corsair MP600 PRO NH 2 TB (artifacts) |
| OS | Windows 11 Pro 25H2 (build 26200.8973) |
| .NET | 10.0.10 (win-x64) |

Presets: **Default** = 2160², 3 px cells, 4 bits (robust); **Dense** = 2160², 2 px, 6 bits;
**Max4K** = 3840x2160, 1 px, 6 bits; **Max4K-R10** = Max4K + 10% parity images.

| Size | Default enc / dec | Dense | Max4K | Max4K-R10 |
|---:|---:|---:|---:|---:|
| 1 KB | 11 / 50 ms | 17 / 54 ms | 64 / 110 ms | 81 / 124 ms |
| 1 MB | 73 / 44 ms | 130 / 60 ms | 79 / 122 ms | 96 / 126 ms |
| 10 MB | 267 / 161 ms | 213 / 97 ms | 100 / 137 ms | 99 / 141 ms |
| 100 MB | 2.47 / 1.06 s | 1.34 / 0.46 s | **0.28 / 0.28 s** | 0.35 / 0.37 s |
| 1 GB | 16.57 / 9.37 s | 10.93 / 4.29 s | **2.24 / 2.42 s** | 2.71 / 2.58 s |

One full-matrix run: all 40 cases (10 sizes x 4 presets) measured back to back on the same build,
so nothing here is stitched together from different sittings. BenchmarkDotNet v0.15.8 means,
Monitoring strategy, 3 iterations per case, .NET 10.0.10, decoded output SHA-verified every
iteration. Macro IO benchmarks are noisy at this iteration count — some small cases carry wide
error bars — so treat sub-20 ms differences as noise. A relative **perf gate** runs on every PR:
base and head builds race the same 30 MB round trip, failing on a >30% median regression.

The crossover: below ~1 MB every preset needs one image, so the smaller Default canvas wins on
fixed cost; at scale, Max4K packs ~13x more payload per pixel, so 100 MB is 22 images instead
of 495 — which dominates end-to-end time too, since every image is a capture. At 1 GB it is 220
images against 5,068, and Max4K also finishes the codec work ~5.5x faster (4.7 s vs 25.9 s
round trip).

### Charts

Codec time is only half the story: on a real transfer the *capture cadence* dominates, so the
last two charts add the per-image cost of actually getting each shard onto the receiving screen.
All four are log-log, generated from the same measurements as the table below.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/codec-time-dark.svg">
  <img alt="Codec time by file size: encode (solid) and decode (dashed) for all four presets, log-log" src="docs/benchmarks/codec-time-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/throughput-dark.svg">
  <img alt="Codec round-trip throughput in MB/s of payload per second of codec time" src="docs/benchmarks/throughput-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/transfer-manual-dark.svg">
  <img alt="Estimated end-to-end transfer time with manual capture at 3 seconds per image" src="docs/benchmarks/transfer-manual-light.svg">
</picture>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/benchmarks/transfer-auto-dark.svg">
  <img alt="Estimated end-to-end transfer time with automated capture at 0.5 seconds per image" src="docs/benchmarks/transfer-auto-light.svg">
</picture>

The two transfer charts are where the density presets earn their keep: they are codec time plus
`images x seconds-per-image`, so they rank by **image count**, not by codec speed. At 100 MB that
is a 495-image Default set against a 22-image Max4K one — about 25 minutes of hand-driven capture
versus about 1 minute. The presets differ by roughly 3 seconds of codec time at that size, which
is simply irrelevant next to a 24-minute difference in capture. At 1 GB the same effect is
brutal: 5,068 images is over four hours of manual capture, where Max4K's 220 images is about
eleven minutes.

### All measurements

Every case in the matrix. **Images** is the shard count (`+Np` = parity images); **Est. manual**
and **Est. auto** add capture cadence at 3 s and 0.5 s per image to the measured codec time.
Encode and decode are BenchmarkDotNet means.

<!-- BENCH:TABLE:START -->
| Size | Preset | Images | Encode | Decode | Codec MB/s | Est. manual (3 s/img) | Est. auto (0.5 s/img) |
|---|---|---:|---:|---:|---:|---:|---:|
| 1KB | Default | 1 | 11.1 ms | 50.1 ms | 0.016 | 3.06 s | 561.2 ms |
| 1KB | Dense | 1 | 17.1 ms | 53.6 ms | 0.014 | 3.07 s | 570.8 ms |
| 1KB | Max4K | 1 | 63.7 ms | 109.7 ms | 0.006 | 3.17 s | 673.4 ms |
| 1KB | Max4K-R10 | 1+1p | 81.1 ms | 124.1 ms | 0.005 | 6.21 s | 1.21 s |
| 10KB | Default | 1 | 15.1 ms | 53.8 ms | 0.142 | 3.07 s | 568.9 ms |
| 10KB | Dense | 1 | 17.3 ms | 56.7 ms | 0.132 | 3.07 s | 574 ms |
| 10KB | Max4K | 1 | 78.7 ms | 127.9 ms | 0.047 | 3.21 s | 706.6 ms |
| 10KB | Max4K-R10 | 1+1p | 90.2 ms | 130.6 ms | 0.044 | 6.22 s | 1.22 s |
| 100KB | Default | 1 | 57.2 ms | 49.7 ms | 0.914 | 3.11 s | 606.9 ms |
| 100KB | Dense | 1 | 33.5 ms | 59.4 ms | 1.1 | 3.09 s | 592.8 ms |
| 100KB | Max4K | 1 | 89.4 ms | 123.6 ms | 0.458 | 3.21 s | 713.1 ms |
| 100KB | Max4K-R10 | 1+1p | 90.5 ms | 137.5 ms | 0.428 | 6.23 s | 1.23 s |
| 500KB | Default | 3 | 83.8 ms | 60.5 ms | 3.4 | 9.14 s | 1.64 s |
| 500KB | Dense | 1 | 113.3 ms | 54.9 ms | 2.9 | 3.17 s | 668.2 ms |
| 500KB | Max4K | 1 | 80 ms | 129.4 ms | 2.3 | 3.21 s | 709.3 ms |
| 500KB | Max4K-R10 | 1+1p | 97.7 ms | 142.6 ms | 2 | 6.24 s | 1.24 s |
| 1MB | Default | 5 | 73.2 ms | 43.6 ms | 8.6 | 15.12 s | 2.62 s |
| 1MB | Dense | 2 | 130.3 ms | 60 ms | 5.3 | 6.19 s | 1.19 s |
| 1MB | Max4K | 1 | 78.9 ms | 121.5 ms | 5 | 3.2 s | 700.4 ms |
| 1MB | Max4K-R10 | 1+1p | 96.1 ms | 126.1 ms | 4.5 | 6.22 s | 1.22 s |
| 10MB | Default | 50 | 266.8 ms | 161.4 ms | 23.3 | 2.5 min | 25.43 s |
| 10MB | Dense | 15 | 212.5 ms | 96.8 ms | 32.3 | 45.31 s | 7.81 s |
| 10MB | Max4K | 3 | 100.3 ms | 136.7 ms | 42.2 | 9.24 s | 1.74 s |
| 10MB | Max4K-R10 | 3+1p | 98.6 ms | 140.9 ms | 41.8 | 12.24 s | 2.24 s |
| 100MB | Default | 495 | 2.47 s | 1.06 s | 28.4 | 24.8 min | 4.2 min |
| 100MB | Dense | 147 | 1.34 s | 461.9 ms | 55.5 | 7.4 min | 1.3 min |
| 100MB | Max4K | 22 | 278.8 ms | 282.5 ms | 178 | 1.1 min | 11.56 s |
| 100MB | Max4K-R10 | 22+3p | 347 ms | 365.8 ms | 140 | 1.3 min | 13.21 s |
| 250MB | Default | 1238 | 4.47 s | 2.22 s | 37.4 | 1.03 h | 10.4 min |
| 250MB | Dense | 366 | 3.39 s | 1.06 s | 56.2 | 18.4 min | 3.1 min |
| 250MB | Max4K | 54 | 603.2 ms | 680 ms | 195 | 2.7 min | 28.28 s |
| 250MB | Max4K-R10 | 54+6p | 636 ms | 704.6 ms | 186 | 3 min | 31.34 s |
| 500MB | Default | 2475 | 8.34 s | 4.75 s | 38.2 | 2.07 h | 20.8 min |
| 500MB | Dense | 732 | 6.28 s | 2.12 s | 59.5 | 36.7 min | 6.2 min |
| 500MB | Max4K | 108 | 1.12 s | 1.31 s | 206 | 5.4 min | 56.43 s |
| 500MB | Max4K-R10 | 108+11p | 1.23 s | 1.31 s | 196 | 6 min | 1 min |
| 1GB | Default | 5068 | 16.57 s | 9.37 s | 39.5 | 4.23 h | 42.7 min |
| 1GB | Dense | 1499 | 10.93 s | 4.29 s | 67.3 | 1.25 h | 12.7 min |
| 1GB | Max4K | 220 | 2.24 s | 2.42 s | 220 | 11.1 min | 1.9 min |
| 1GB | Max4K-R10 | 220+22p | 2.71 s | 2.58 s | 194 | 12.2 min | 2.1 min |
<!-- BENCH:TABLE:END -->

### Running the benchmarks

`tests/QrShard.Benchmarks` is a [BenchmarkDotNet](https://benchmarkdotnet.org/) suite measuring
encode and decode across file sizes **1 KB – 1 GB** and the four presets:

```
cd tests/QrShard.Benchmarks
dotnet run -c Release                      # full matrix — ~14 min on the machine above, ~5 GB temp disk
QRSHARD_BENCH_SIZES=1KB,1MB,100MB QRSHARD_BENCH_PRESETS=Default,Max4K dotnet run -c Release
dotnet run -c Release -- --graphs-only     # regenerate graphs from persisted results
dotnet run -c Release -- --readme-assets   # refresh this README's charts + table
dotnet run -c Release -- --fps-probe       # decode frame rate / throughput (Decode frame rate table)
```

Results persist and **merge across runs**; output includes the machine-spec header and a
self-contained `transfer-graphs.html`. That merge is what lets the matrix be measured in several
sittings — but it also means a partial re-run leaves the untouched sizes at their older numbers,
silently mixing builds in one table. After perf work, re-measure every size you intend to
publish (a stale row usually gives itself away as a non-monotonic kink in the charts).

`--readme-assets` re-exports what you see above from those same persisted results: one
standalone SVG per chart per colour scheme into [`docs/benchmarks/`](docs/benchmarks/), and the
measurements table spliced back into this file between its `BENCH:TABLE` marker comments. The
charts are emitted with every presentation attribute inlined — GitHub's SVG sanitizer strips
`<style>` blocks, which would otherwise render them as unstyled black shapes.

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
  **thread-local pixel canvas** per worker. Worker count adapts to the configured pixel budget.
- **Custom fast PNG writer AND reader** ([FastPng.cs](src/QrShard/FastPng.cs),
  [FastPngReader.cs](src/QrShard/FastPngReader.cs)): the writer streams one IDAT straight from
  the render buffer (row-blit rendering, Up filter — or raw *stored* deflate blocks for
  incompressible 1 px cells); the reader handles the truecolor subset every screenshot tool
  emits, ~2x faster than a general decoder, falling back to ImageSharp for anything else.
- **Streaming both ways**: incompressible inputs are memory-mapped and read per-chunk by the
  encode workers; reassembly streams chunks → decrypt/decompress → disk with an incremental
  SHA-256, so neither side materializes the file twice. Encryption is the one exception, because
  AES-GCM authenticates the whole message before releasing any of it and therefore needs the
  payload contiguous: the encoder reads the file straight into the cipher blob, and the decoder
  gathers the shard chunks into one. Both then **encrypt/decrypt that blob in place** rather than
  pairing it with a second full-size buffer — so encoding an encrypted payload peaks at one copy
  of it, and decoding at the shard chunks plus one.
- **Table-driven Reed-Solomon with SIMD on both paths**: 16 codewords per `Vector128` lane for
  the decode-side syndrome scan *and* the encode-side LFSR (nibble-shuffle product tables);
  clean codewords skip the scalar decoder entirely. Cross-shard parity and fountain coding use
  SIMD GF(2⁸) multiply-accumulate. Grid sampling uses precomputed per-row/per-column
  coordinate tables — per-cell work is array lookups, not floating-point math.
- **GC discipline**: server GC; per-worker scratch buffers everywhere; exact-size buffers; the
  camera refinement path evaluates its interpolation fields with zero per-pixel allocations.

### Image library choice

Decode must parse arbitrary screenshots from unknown tools — that needs a mature fallback:
**ImageSharp** (pure managed, cross-platform; v4, used under a Six Labors community license —
see below). The hot paths (PNG in both directions) are hand-rolled; everything else goes
through ImageSharp with lossless speed-tuned settings.

## Capture tips

- Display the image at **100% zoom** and screenshot it (a cropped region capture is fine — just
  include the whole black frame with a little margin).
- `-r auto` sizes shards to your monitor; run `qrshard calibrate` once to find the densest
  settings *your* capture chain survives.
- For cell sizes below 3 px the capture must be pixel-perfect: avoid fractional display scaling
  (125%/150%) and browser zoom.
- Cursors, small overlays, and high-quality JPEG re-encoding are absorbed by ECC; use
  `info --heatmap` to see where a problem capture is actually damaged.
- Rotation/perspective needs `--camera` shards; the default screenshot profile assumes an
  axis-aligned capture. For recordings, `-F 100` fountain coding makes lost frames irrelevant.

## Building and testing

Requires the .NET 10 SDK. `dotnet build -c Release` at the solution root; `./publish.ps1` for
standalone binaries.

Three workflows run on every push and pull request (CI, Interop, Package), a fourth on pull
requests only (Perf gate), and a fifth on a weekly schedule (Fuzz):

| Workflow | What it guards |
|---|---|
| **CI** | Build + the full suite on windows-latest, ubuntu-latest, ubuntu-24.04-arm and macos-latest — every platform a release binary is published for. Each job renders totals, a per-class breakdown and the slowest tests into the run summary |
| **Interop** | Four encoders x four decoders: shards encoded on each OS/arch must decode on every other, forcing parity reconstruction (where the x64 and arm64 GF(2⁸) paths could disagree) and covering the encrypted path |
| **Package** | Packs both NuGet packages and consumes them from *outside* the repo — compiles the readme's own code sample against the packed package, round-trips through the public API, and installs the dotnet tool |
| **Perf gate** | Base and head builds race the same 30 MB round trip; a >30% median regression fails |
| **Fuzz** | Weekly, 20 000 iterations of structure-aware fuzzing over every parser that consumes untrusted bytes |

Tagged releases additionally build the Native-AOT binaries, **run** each one (version, self-test
and a real round trip with a deleted image rebuilt from parity) before attaching it, and only
publish to nuget.org once all four have passed — publication is the one irreversible step, so it
waits on everything else.

ImageSharp 4.x validates a license at build time. License keys are personal and **not
committed**: obtain your own (free community licenses for qualifying open-source use at
https://sixlabors.com/pricing/) and either drop `sixlabors.lic` at the solution root
(gitignored) or set the `SixLaborsLicenseKey` environment variable (CI uses the
`SIXLABORS_LICENSE_KEY` repository secret). The license is build-time only; published binaries
and end users need nothing.

- `dotnet test` — the xUnit suite, 610 tests in ~20 s. Covers the codec math (CRC vectors, GF(2⁸) field
  laws, Reed-Solomon incl. errors-and-erasures, interleaving, Cauchy and fountain erasure
  codes), round trips across every density/ECC/format/flag combination, simulated screenshots
  and camera photos, non-truecolor capture shapes, video recordings (duplicates, torn frames,
  early stop, camera video with pose drift), encryption, archives, sessions, watch mode,
  fusion, calibration, randomized robustness fuzzing of every untrusted-input parser, and the
  CLI.
- **Cross-version interop**: `tests/QrShard.Tests/golden/` holds frozen shard fixtures encoded by
  the tagged binaries themselves; the suite asserts the current decoder still reconstructs every
  one byte-for-byte, so a shard encoded with an old release always decodes.
  `golden/versions.json` lists which versions must be covered — one per released minor line,
  since the wire format is versioned by a metadata nibble that patch releases don't touch. It is
  read by both the tests and `golden/regenerate.ps1`, so the fixtures on disk and the set the
  tests demand cannot drift apart; a listed version with no fixtures fails the suite, and so does
  a fixture directory nothing lists. Two further checks keep the list honest without anyone
  having to remember: the covered minor lines must have no gaps, and they must reach the line
  below the shipping version — which is how a minor that ships without fixtures gets caught. (It
  cannot demand the *current* version: a version-bump PR sets the csproj before the tag exists,
  and `regenerate.ps1` builds each version from a worktree of its tag, so such a PR structurally
  cannot carry its own fixtures.) After tagging a new minor, run `regenerate.ps1` and commit what
  it produces — it skips versions already present, because the fixtures are frozen.
- `qrshard test` — end-to-end self-test at real resolutions, including simulated screenshots
  with cursor damage and a cross-shard recovery scenario.
