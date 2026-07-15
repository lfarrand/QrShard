# QrShard

Encodes any file into a series of high-density, QR-style PNG images that can be captured by
screenshot on another device and reconstituted back into the original file, bit-for-bit.

The format is custom (not QR-standard) and tuned for screen-to-screenshot transfer rather than
camera capture, which allows far higher data density than a real QR code.

## Usage

```
dotnet run --project src/QrShard -c Release -- <command> ...
```

```
qrshard encode <file> [options]     Split a file into shard images.
  -o, --out <dir>          Output folder (default: <file>.shards next to the input)
  -r, --resolution <px>    Image size: "auto" (the default) detects the primary monitor's
                           native resolution so shards fill the screen they'll be captured
                           from; or one number (square) or WxH, 700-16384, to override —
                           e.g. a smaller size shows the code surrounded by padding
  -c, --cell <px>          Data cell size in pixels, 1-64 (default: 3)
  -b, --bits <n>           Bits per cell / color density, 1-8 (default: 4)
  -e, --ecc <n>            Reed-Solomon parity per 255-byte block, even, 0-64
                           (default: 16 ≈ 6% overhead, fixes 8 bad bytes per block)
  -R, --recovery <pct>     Add parity IMAGES so whole missing/damaged images can be rebuilt
                           without recapture; pct% extra images, 0-100 (default: 0)
  -f, --format <fmt>       Lossless image format: png, bmp, tga, qoi, webp, tiff
                           (default: png, written by the built-in fast PNG writer)
  --no-compress            Skip deflate compression of the payload

qrshard decode <folder|images...> [-o <file>]
                           Reconstitute the original file from captured images.
qrshard info <image>       Show and validate a single shard image.
qrshard test               Round-trip self-test, including simulated screenshots.
```

An optional `appsettings.json` next to the executable holds preferences and machine tuning
(comments are allowed in it, as in standard .NET appsettings files; every value is documented
inline there). Invalid values fail loudly rather than silently defaulting. Settings:

- **`EncodeDefaults`** — your preferred defaults for every `encode` flag (`Resolution`, `CellPx`,
  `BitsPerCell`, `EccParity`, `RecoveryPercent`, `ImageFormat`, `Compress`); a flag on the
  command line always wins over the file.
- **`ShardFolderSuffix`** — the `<file>.shards` folder suffix used when `-o` isn't given.
- **`PngCompressionLevel`** — deflate level for the built-in PNG writer where compression pays
  off (cell >= 2 px): `Optimal` (default), `Fastest`, `SmallestSize`, `NoCompression`. 1 px
  cells always use `Fastest` (noise-like content is incompressible by construction).
- **`PayloadCompressionLevel`** — deflate level for compressing the file payload itself.
- **`EncodeMemoryBudgetMB`** / **`DecodeMaxParallelism`** — machine tuning for the parallel
  workers (defaults: ~2 GB pixel-buffer budget; decode auto = cores capped at 16).

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

Measured on a desktop CPU (32 logical cores, parallel codec):

- **100 MB @ 3840x2160 / cell 1 / 6 bits / 10% recovery → 24 images, encoded in ~1 s, decoded
  in ~1.2 s** — roughly 100 MB/s encode, 85 MB/s decode. Deleting whole images before decoding
  still produces a byte-identical, SHA-256-verified file (rebuilt from parity).
- 100 MB @ robust defaults → 485 images, ~2.8 s encode / ~2.4 s decode.

**Can you transfer a 300 MB zip? Yes.** At 4K density it is ~65 images; with `-R 10` you also get 7
parity images so any 7 can be lost. The codec itself is never the bottleneck (a few seconds for
300 MB). End-to-end time is dominated by *capture cadence*: at a manual ~3 s per screenshot, ~72
images ≈ **3-4 minutes** of capturing (~1 MB/s effective). An automated capture loop (a script
paging through the images and screenshotting each) pushes that several-fold. At the robust default
density (~212 KB/image) the same 300 MB would instead be ~1,450 images — fine for an automated
loop, tedious by hand — which is why a dense config on a 4K display is the right choice for large
files. Hard limits: ≤ 1.5 GB per file (in-memory codec); display size caps per-image resolution
(the code must be shown at 100% zoom to be pixel-perfect).

### Codec performance design

- **One flat parallel loop over all images** (data + parity together, no phase barrier), with a
  **thread-local pixel canvas** per worker — reused across images instead of a fresh 15-50 MB
  allocation each. Worker count adapts to a ~2 GB pixel-buffer budget, so normal resolutions use
  every core while 16K images cap themselves.
- **PNG settings tuned to shard content**: level-1 deflate (dense cell data is nearly
  incompressible; ImageSharp's default level-6 adaptive filtering cost ~3x the encode time and
  *hurt* compression of noise-like data), with the "Up" filter only where cell rows repeat.
- **Decode workers reuse scratch buffers** (pixels + flood-fill visited map) — decoding 100 MB
  previously allocated ~9.5 GB of garbage; now ~2 buffers per worker, and the folder decode uses
  up to 16 workers.
- **Table-driven Reed-Solomon**: the encoder's LFSR inner loop XORs a precomputed 256-row
  generator table instead of doing per-symbol field multiplies; syndrome computation (the
  every-codeword hot path on decode) uses per-α multiplication tables.
- **SIMD GF(2⁸) for cross-shard parity**: `MulAdd` uses the nibble-shuffle technique
  (two 16-entry product tables as byte-shuffle sources, 16 bytes per step) via `Vector128`.
- **No nested parallelism**: per-image FEC is sequential inside the already-parallel per-image
  loops, avoiding scheduler contention.
- **GC pressure**: server GC (the workload is many parallel allocating workers); per-worker
  scratch extends beyond the pixel canvas to the stream/cell staging buffers, the FEC recovery
  buffer, and the 128 KB nearest-color LUT; reassembly and decompression write into exact-size
  output buffers (no MemoryStream doubling or ToArray copies); cross-shard stripe chunks reuse
  one buffer set. Cell read/write uses a two-byte-window fast path instead of per-bit loops.
- **Streaming encoder**: incompressible inputs (the common big-transfer case) are memory-mapped
  and read per-chunk by the parallel workers — the file is never materialized as a managed
  array. Compressible inputs still materialize (the deflated stream must exist somewhere).
- **SIMD syndrome scan**: the per-image FEC buffer is symbol-interleaved, so 16 codewords'
  syndromes are computed together — one Vector128 lane per codeword — and clean codewords
  (nearly all of them) skip the scalar decoder entirely.
- **Custom fast PNG writer** for the default format (see the library investigation above), and
  a dedicated pooled ImageSharp memory allocator for the non-PNG encoders.

Result (100 MB, 32 cores, BenchmarkDotNet means): Max4K encode 18.2 s → ~0.7 s, Max4K-R10
24.4 s → ~0.7 s; default-preset encode 6.5 → 2.3 s, decode 4.3 → 1.7 s. Decode allocations
fell from ~9.5 GB to ~0.9 GB. All presets remain byte-verified.

Deflate compression is applied automatically when a fast mid-file sample suggests it will help,
so compressible files transfer in correspondingly fewer images (already-compressed archives skip
straight to encoding).

## Image formats

Shards can be written in any of six lossless container formats (`-f`); the container is
transport-only — decoding, ECC, and recovery are identical through all of them. Measured on a
100 MB transfer at the default density (32 cores):

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

## Image library: investigation and the custom PNG writer

The codec has asymmetric needs. **Decode** must parse arbitrary screenshots produced by unknown
tools — that genuinely needs a mature, battle-tested image library. **Encode** does not: we own
every pixel and just need to serialize a buffer losslessly. Options considered:

- **ImageSharp** (current): pure managed, cross-platform, decodes every format we care about,
  pluggable memory allocator. Kept for all decoding and for the non-PNG encoders. Pinned to
  3.1.x (4.0 requires a commercial license key at build time).
- **SkiaSharp**: fast native codecs, but a ~10 MB native dependency per RID, no TGA/QOI encode,
  and interop copies at the boundary — no win for this workload.
- **Magick.NET**: broadest format support, but a heavyweight native dependency and
  process-global configuration; overkill for six lossless formats.
- **System.Drawing.Common**: Windows-only since .NET 6 and effectively deprecated for new code.
- **StbImageSharp/StbImageWriteSharp**: tiny, but decode-side robustness and encode-side
  compression quality are both below what the transfer needs.
- **Custom**: viable precisely once, for the encode hot path — implemented as
  [FastPng.cs](src/QrShard/FastPng.cs), ~150 lines: 8-bit truecolor, a fixed None/Up filter,
  zlib via .NET's zlib-ng, one IDAT streamed straight from the render buffer to the file with
  an incrementally-computed chunk CRC. Output is standard PNG readable by anything (verified
  pixel-identical through ImageSharp in tests). It removed the last library overhead from the
  encode path and produces smaller files than ImageSharp's level-1 encoder did.

A fully custom *container* format was rejected: shards must be displayable by ordinary OS image
viewers on the sending machine, so the container must be a mainstream standard.

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
   unlucky loss patterns. `-R 10` tolerates losing ~10% of the images. This is the layer that
   matters for large multi-image transfers, where finding and recapturing one bad screenshot out
   of hundreds is the real pain point.
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

## Image format

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

## Capture tips

- Display the PNG at **100% zoom** and screenshot it (a cropped region capture is fine — just
  include the whole black frame with a little margin).
- On a 4K display, encode with `-r 3840x2160` and view fullscreen for maximum per-image payload.
- For cell sizes below 3 px the capture must be pixel-perfect: avoid fractional display scaling
  (125%/150%) and browser zoom.
- Cursors, small overlays, and high-quality JPEG re-encoding are absorbed by ECC; raise
  `--ecc` (e.g. 32) for hostile conditions, or lower it toward 0 for maximum capacity.
- Rotation/perspective is not supported — this is a screenshot format, not a camera format.

## Building and testing

Requires the .NET 10 SDK. `dotnet build -c Release` at the solution root.
Uses SixLabors.ImageSharp 3.1.x (pinned below 4.0, which requires a commercial license key at
build time; 3.1 is free under the Six Labors Split License for open source and small business).

- `dotnet test` — 217 xUnit tests, ~2 s, **97% line / 92% branch coverage** of the codec
  (`dotnet test --collect:"XPlat Code Coverage"` to reproduce). Covers: CRC check-vectors,
  GF(2⁸) field laws and matrix inversion, Reed-Solomon (max-error correction, beyond-capacity
  detection, shortened codewords), FEC interleaving (burst and scatter damage), cross-shard
  erasure coding (exhaustive loss-pattern MDS verification, mixed data/parity loss, degenerate
  and max-size stripes), palette construction/classification, bit packing, layout geometry incl.
  non-square, metadata strip (every-single-bit-flip rejection), header robustness
  (corruption/truncation/unicode/parity), full round trips across density and ECC configs,
  simulated captures (padded/rescaled/cropped/dark-background/JPEG), damage recovery (cursor,
  banner, dead strips, excessive damage fails cleanly), whole-image recovery (deleted and
  destroyed images rebuilt from parity, loss-beyond-budget fails cleanly), shard-set handling
  (missing, duplicate, corrupt-among-good, multi-file folders, overwrite protection), and the CLI.
- `qrshard test` — end-to-end self-test at real resolutions (up to a 14 MB single image),
  including simulated screenshots with cursor damage and a cross-shard recovery scenario.

## Benchmarks

`tests/QrShard.Benchmarks` is a [BenchmarkDotNet](https://benchmarkdotnet.org/) suite measuring
encode (file → shard PNGs on disk) and decode (PNGs → SHA-verified file) across file sizes
**1 KB, 10 KB, 100 KB, 500 KB, 1 MB, 10 MB, 100 MB, 250 MB, 500 MB, 1 GB** and four config
presets: `Default` (2160², cell 3, 4 bits), `Dense` (2160², cell 2, 6 bits), `Max4K`
(3840x2160, cell 1, 6 bits), and `Max4K-R10` (Max4K + 10% cross-shard parity). Payloads are
generated deterministically (incompressible, zip-like) and every decode iteration is verified
byte-identical via SHA-256 outside the timed region.

```
cd tests/QrShard.Benchmarks
dotnet run -c Release                      # full matrix — takes ~2 hours, needs ~5 GB temp disk
                                           #   and ~8 GB free RAM for the 1 GB cases

# Trim either axis without editing code:
QRSHARD_BENCH_SIZES=1KB,1MB,100MB QRSHARD_BENCH_PRESETS=Default,Max4K dotnet run -c Release

dotnet run -c Release -- --graphs-only     # regenerate graphs from persisted results, no runs
```

Long-running macro-benchmarks use the Monitoring run strategy (1 warmup + 3 measured iterations
per case) rather than BenchmarkDotNet's micro-benchmark defaults, which would take days at 1 GB.
Results persist in `results/transfer-results.json` and **merge across runs** — benchmark the
small sizes now and the 1 GB cases overnight, and the graphs always show everything measured
so far (latest measurement of a case wins).

Results land in `BenchmarkDotNet.Artifacts/results/`:

- the standard BenchmarkDotNet table (console + GitHub-markdown + CSV);
- a full machine-spec header on the HTML report (CPU model/speed, motherboard + firmware
  revision, RAM sticks/type/speed, physical disks, Windows edition + build.revision, .NET
  runtime version), gathered via WMI at report time so the numbers carry their context;
- **`transfer-graphs.html`** — self-contained SVG charts generated from the run: codec time vs
  file size (log-log), estimated end-to-end transfer time including screenshot cadence (manual
  3 s/image and automated 0.5 s/image), codec throughput, and the full numbers table;
- `[RPlotExporter]` boxplot/density `.png` graphs as described on the BenchmarkDotNet homepage —
  these require [R](https://www.r-project.org/) with `Rscript` on `PATH`; without R the exporter
  is skipped (the HTML graphs are produced regardless).
