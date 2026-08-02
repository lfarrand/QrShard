# QrShard

[![CI](https://github.com/lfarrand/QrShard/actions/workflows/ci.yml/badge.svg)](https://github.com/lfarrand/QrShard/actions/workflows/ci.yml)

QrShard transfers files between machines through the screen: it encodes any file (or folder)
into a series of high-density, QR-style images which are displayed on one machine, captured on
another — by screenshot, phone photo, screen recording, or a live webcam — and reconstituted
back into the original file, **bit-for-bit, verified by SHA-256**.

The image format is custom (not QR-standard) and tuned for screen-to-screenshot transfer.
Because a screenshot is a lossless pixel copy, each image can be vastly denser than a real QR
code: from ~212 KB per image at the robust default, to ~4.9 MB with the Max4K profile, and up to
**~6.5 MB per image** at 8-bit density on a 4K display — so a 100 MB file fits in 22 Max4K
screenshots and a 300 MB zip in ~65. Layered error correction
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

The codec is pure managed .NET 10 — no native codec dependency — and the wire format is
platform-agnostic by construction. The **Interop** workflow runs all sixteen pairs of
{win-x64, linux-x64, linux-arm64, osx-arm64} encoders and decoders on every pull request. Each pair
deletes a data image so decode must rebuild it through Cauchy parity; the encrypted path also pins
cross-platform construction of AES-GCM associated data.

| Target | Current CI/release evidence | Monitor auto-detection (`-r auto`) | Benchmark machine spec |
|---|---|---|---|
| Windows x64 (`windows-2025`) | build/test + interop + `win-x64` Native-AOT release smoke | `EnumDisplaySettings` physical pixels | WMI |
| Linux x64 (`ubuntu-22.04`) | build/test + interop + `linux-x64` Native-AOT release smoke | `xrandr` on X11/XWayland; headless fallback | OS + .NET + cores only |
| Linux arm64 (`ubuntu-24.04-arm`) | build/test + interop + `linux-arm64` Native-AOT release smoke | `xrandr` on X11/XWayland; headless fallback | OS + .NET + cores only |
| macOS arm64 (`macos-15`) | build/test + interop + `osx-arm64` Native-AOT release smoke | CoreGraphics Retina pixel dimensions | OS + .NET + cores only |
| macOS x64 (`osx-x64`) | local JIT single-file publish target; **not** in CI, interop, or tagged releases | same CoreGraphics implementation; expected, not workflow-verified | OS + .NET + cores only |

"Verified" here means hosted build/test, cross-runner wire interop, and release-binary smoke tests.
Hosted runners do **not** exercise a physical monitor, webcam, capture card, real Retina/DPI
scaling, or a real X11 desktop. Treat live capture and display auto-detection as implemented but
hardware-dependent; run `qrshard calibrate` on the actual sender/receiver setup.

Video decoding and the live receiver additionally need [ffmpeg](https://ffmpeg.org) on an absolute,
trusted `PATH` entry (or pinned with `FfmpegPath`)
(animated png/gif/webp recordings decode natively without it).

The tagged Native-AOT Linux binaries have explicit libc floors: **glibc 2.35** for `linux-x64`
(built on Ubuntu 22.04) and **glibc 2.39** for `linux-arm64` (built on Ubuntu 24.04). On an older
distribution, use the .NET tool package with a compatible .NET 10 runtime or build locally. Linux
standalone/local-publish binaries also require a system ICU installation (normally the distribution's
`libicu` package): full Unicode normalization/casing is a path-safety boundary, so invariant
globalization is deliberately not used or bundled.

## Installing

- **dotnet tool**: `dotnet tool install -g QrShard.Tool` → the `qrshard` command (needs the
  .NET 10 runtime).
- **Standalone binaries**: tagged releases attach Native-AOT single-file binaries for
  win-x64 / linux-x64 / linux-arm64 / osx-arm64 — no .NET install needed (see the Linux glibc
  floors above). The executable is named `QrShard.exe` on Windows and case-sensitive `QrShard`
  on Unix. Beginning with v1.6.2, GitHub stores signed SLSA build-provenance and SPDX 2.2 SBOM
  attestations for the archives and release packages. These attestations authenticate the exact
  GitHub Release bytes; they are not platform code signatures. Windows is not Authenticode-signed,
  and macOS output is expected to be ad-hoc only rather than Developer ID signed or notarized.
- **Local single-file publish**: `./publish.ps1` (or `.sh`) creates self-contained, single-file
  **JIT** builds for win-x64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64. These convenient
  local builds are not the Native-AOT artifacts produced by the tagged-release workflow. Each RID
  is built in a private sibling stage; replacing an existing local output keeps a rollback copy
  until the new directory has been installed. The scripts hold `publish/.qrshard-publish.lock`, so
  a concurrent invocation fails before changing output. An abnormally terminated publisher leaves
  the lock for inspection and verified manual removal rather than guessing that it is stale.
- **As a library**: `dotnet add package QrShard.Core` — the embeddable codec, wire-compatible with
  the CLI. `QrShardCodec.EncodeFile` / `DecodeImages` for one-shot use, plus `QrShardDecodeSession`
  for **incremental** decoding: feed captures (files or in-memory image bytes) as they arrive,
  query which images are still missing, and assemble the moment the set is recoverable. Its default
  retained-shard ceiling is 4,000 decimal MB; embeddings can set a smaller explicit bound with
  `new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: 512)`. A refused addition leaves
  session state unchanged and reports the resource limit in `QrShardAddResult.Error`.
- **From source**: `dotnet run --project src/QrShard -c Release -- <command>` (see
  [Building](#building-and-testing) for the ImageSharp license note).

### Verifying a v1.6.2-or-later tagged release

The current release workflow first applies these controls to v1.6.2. Older published releases do
not have the SBOM, checksum, or attestation assets described here. Install the
[GitHub CLI](https://cli.github.com/), download a v1.6.2-or-later binary archive or `.nupkg`, and
constrain verification to this repository's release workflow and tag:

```sh
tag=vX.Y.Z
asset=qrshard-linux-x64.tar.gz

gh attestation verify "$asset" --repo lfarrand/QrShard \
  --signer-workflow lfarrand/QrShard/.github/workflows/release.yml \
  --source-ref "refs/tags/$tag"

gh attestation verify "$asset" --repo lfarrand/QrShard \
  --signer-workflow lfarrand/QrShard/.github/workflows/release.yml \
  --source-ref "refs/tags/$tag" \
  --predicate-type https://spdx.dev/Document/v2.2

gh release verify-asset "$tag" "$asset" --repo lfarrand/QrShard
```

The first command verifies SLSA provenance; the second verifies the artifact-specific SPDX
document and its staged file hash. Each Native-AOT archive has its own RID-specific restored graph,
including that RID's Native-AOT runtime/compiler packs; each `.nupkg` has a separate ordinary
framework-dependent package graph. The third command verifies the asset digest and its association
with the immutable GitHub Release. `SHA256SUMS` and the six standalone SBOM JSON files have
provenance and immutable-release coverage, but are not themselves subjects of an SBOM predicate.
`SHA256SUMS` remains a plaintext convenience index, but its bytes and every listed release file are
also covered by provenance attestations. Releases produced by the current workflow, beginning with
v1.6.2, are immutable after publication: GitHub locks their assets and tag and creates a release
attestation.

The `.nupkg` attached to GitHub Releases is the exact pre-publication byte sequence covered here.
NuGet.org subsequently repository-signs an uploaded package, which changes its bytes. NuGet clients
can verify that repository signature as a separate trust boundary; enforcement depends on the
platform and the client's [signed-package verification policy](https://learn.microsoft.com/dotnet/core/tools/nuget-signed-package-verification).

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
just include the whole black frame with a little margin). For large files add `-R 10` to add
roughly 10 parity images per 100 data images; recovery capacity is allocated and enforced per
stripe.

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

**Video mode — no manual capturing at all.** Add `--video` when encoding and `slideshow.html` is
written next to the shards: open it in any browser, press **Start fullscreen** (the required user
gesture), and it cycles every image forever. Missing/unreadable frames are skipped as erasures
(default 500 ms each; `--interval` to tune). The HTML page is a small **relative manifest**, not a
self-contained copy: keep it beside the shard images and any generated `.slideshow-…-frame-….png`
sidecars. Moving only the HTML file breaks its references. `--slideshow apng` instead writes one
animated PNG, but APNG creation is capped at **256 MiB of decoded RGB frame pixels**; use HTML for
larger sets. On the receiving side, **record the screen** for one full cycle — or point a phone at
it (`--camera` shards decode from handheld video, with the detected pose cached between frames) —
and feed the recording in:

```
qrshard decode recording.mp4 -o holiday-photos.zip
```

Near-duplicate frames are skipped cheaply, torn mid-transition frames fail checksums harmlessly
and come around again next cycle, and decoding **stops early** the moment the collected set is
complete or recoverable. If a file recording still comes up short, it is automatically re-extracted
at a higher frame rate before giving up. Add `-F 100` (fountain coding) when encoding and the
slideshow also cycles random-linear coded frames: a **full-rank set of roughly `stripeData`
frames per stripe** reconstructs the data, so duplicates, dependent rows, and lost or glared
frames simply do not count — the ideal mode for lossy capture chains.

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
| `qrshard encode <file\|folder>... [options]` | Split a file, folder, or multi-input archive into shard images |
| `qrshard send <file\|folder>... [options]` | Encode + open the slideshow in the default browser |
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
| `-o, --out <dir>` | any path | `<input>.shards` beside the input; `bundle.shards` beside the first input for multiple inputs | Output folder for the shard images |
| `-r, --resolution <px>` | `auto`; one number (square); `WxH` — 700–16384 per side | `auto` | Image size. `auto` detects the primary monitor's native resolution so shards fill the screen they'll be captured from |
| `-c, --cell <px>` | 1–64 | 3 | Data cell size in pixels. 3 survives fractional display rescaling; 1 doubles-to-quadruples density but needs pixel-perfect captures |
| `-b, --bits <n>` | 1–8 | 4 | Bits per cell (color density): 2ⁿ palette colors |
| `-e, --ecc <n>` | even, 0–64 | 16 | Reed-Solomon parity bytes per 255-byte block. 16 ≈ 6% overhead; fixes 8 unknown-position bytes/block, up to ~14 when the classifier can flag them (erasures) |
| `-R, --recovery <pct>` | 0–100 | 0 (off) | Extra **parity images** (Cauchy erasure code), calculated as a percentage of data images and distributed per stripe. Loss tolerance is per stripe: `-R 15` adds about 15 parity images per 100 data images (~13% of the resulting set) |
| `-F, --fountain <pct>` | 0–1000 | 0 (off) | **Fountain-coded frames** (random linear code) for video mode: a full-rank set of roughly `stripeData` captured frames per stripe reconstructs the data; dependent/duplicate frames do not count. Mutually exclusive with `-R` |
| `-p, --password <pw>` | any string | off | AES-256-GCM encrypt the payload (PBKDF2-SHA256 key); decode needs the same password. Command-line passwords may be exposed in shell history and process listings |
| `--password-file <file>` | strict UTF-8, optional UTF-8 BOM, ≤64 KiB/4096 characters | off | Read the password without placing it in argv; one trailing line ending is removed. UTF-16/32 and invalid UTF-8 are rejected |
| `--password-stdin` | first line, ≤4096 characters | off | Read the password from standard input. Mutually exclusive with both other password forms |
| `-f, --format <fmt>` | `png`, `bmp`, `tga`, `qoi`, `webp`, `tiff` | `png` | Lossless container format |
| `--camera` | flag | off | Camera profile: finder patterns so shards decode from **photos/handheld video** of the screen; shifts defaults to cell 8 / 2 bits / ECC 32 |
| `--video` | flag | off | Also write a slideshow (see `--slideshow`) for recording-based capture |
| `--slideshow <kind>` | `html`, `apng` | `html` | With `--video`: a relative-manifest `slideshow.html` that must stay beside the shard/sidecar files, or a single `slideshow.apng`. APNG creation refuses sets exceeding 256 MiB of decoded RGB frames; HTML scales without retaining every frame |
| `--open` | flag | off | With `--video`: open the slideshow in the default browser once encoding finishes. `qrshard send` is exactly `encode --video --open` |
| `-i, --interval <ms>` | ≥ 100 | 500 | Slideshow interval per image (both slideshow kinds) |
| `--interleave2` | flag | off | v2 permuted interleave: spreads **vertical** damage (a horizontal banner/overlay) across codewords as well as horizontal. Needs ECC. Signalled by a metadata field from version 4 onward (it rode the version nibble in v3), so a decoder that cannot read it rejects the strip rather than misreading it |
| `--profile <name>` | a name in `appsettings.json` `EncodeProfiles` | — | Apply a named encode preset (see [Configuration](#configuration-appsettingsjson)); explicit flags still override it |
| `--json` | flag | off | Emit the encode result (image/parity counts, geometry, file list, slideshow path) as JSON on stdout instead of the human summary |
| `--dry-run` | flag | off | Print the exact image count and geometry — computed after compression, without rendering — then exit. A guardrail before a large folder silently emits hundreds of PNGs. Honors `--json` |
| `--no-compress` | flag | compression on | Skip Brotli compression of the payload (auto-skipped when a sample shows the file is incompressible) |

**Multiple inputs** are bundled into one archive and extracted on decode:
`qrshard encode report.pdf photos/ notes.txt -o release.shards`. A single folder flattens to the
archive root; multiple inputs keep their names (colliding names are refused, never silently
overwritten). Unknown or misspelled options are rejected up front — a typo'd `--pasword` errors
with a "did you mean" hint rather than silently encoding **unencrypted**.

Archive transfer is deliberately a portable subset. Ordinary files and directories, including
empty directories, are carried. The archive carries Unix regular-file owner/group/other rwx bits,
including executability; extraction applies them subject to the receiver's umask, while .NET strips
setuid, setgid, and sticky special bits. A top-level symbolic link/junction is rejected and
reparse-point entries found inside a selected folder are skipped rather than followed. Hard-linked
paths are copied as independent regular files.
Unsafe platform-specific names and paths that alias by case or Unicode normalization are rejected
at encode and extract time. Ownership, ACLs, extended attributes, alternate data streams,
sparse-file state, directory modes/metadata, and hard-link identity are not portable archive
guarantees.

### `decode` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `-o, --out <path>` | any path | original filename in the current directory (never overwrites — falls back to `<name>.restored<ext>`, then `.restored-2`, `.restored-3`, …) | Where to write the file (a directory for archive payloads). Single files are staged and verified before atomic publication; an archive destination must be absent or empty |
| `-p, --password <pw>` | any string | — | Password for encrypted payloads. A wrong password fails without publishing plaintext, but lengths, image counts, and other cleartext shard metadata remain visible. Command-line values may appear in shell history/process listings |
| `--password-file <file>` | strict UTF-8, optional UTF-8 BOM, ≤64 KiB/4096 characters | — | Read the password from a protected file; one trailing line ending is removed |
| `--password-stdin` | first line, ≤4096 characters | — | Read from standard input. Exactly one password source may be used |
| `--session <file>` | any path | off | Accumulate shards across sittings in an append journal; deleted on success. With an explicit `-o`, the destination must be a fresh, nonexistent path. Conflicting CRC-valid copies become a durable terminal erasure rather than first/last-wins. Applies to **images**; recordings are re-read and reject this option |
| `--watch` | flag | off | Keep watching the folder: decode captures as they land, assemble the moment the set completes; Ctrl+C persists to the session |
| `--clipboard` | flag | off | (Windows) decode the bitmap on the clipboard — snip a displayed shard with Win+Shift+S, no file saving; accumulates with `--session` |
| `--fps <n>` | finite, >0 | 8 | Frame extraction rate when decoding a video recording. If not pinned, an incomplete file recording is automatically re-extracted at 2× then 4× until the set completes |
| `--json` | flag | off | Emit the result as JSON on stdout instead of the human log — the restored files with their **resolved output paths** and lengths, or the per-file completeness status when the set is incomplete |

A plain `decode` of an incomplete folder prints the same per-file status `verify` shows, names
the missing images, points you at `--session`/`--watch`, and exits **3** (distinct from a hard
error) — nothing already collected is lost.

Successful outputs are never streamed into their final pathname. A single file is written to an
unpredictable sibling, length- and SHA-256-verified, then atomically moved into place; replacing an
explicit existing `-o` copies the limited access metadata described below. Archives are verified
as tar bytes first, extracted into a private sibling directory, and published only after every
entry succeeds. An explicit archive destination is refused unless it is absent or empty, so decode
never merges an archive into an existing tree.

An atomic replacement necessarily creates a new filesystem object. For an explicit existing
single-file `-o`, QrShard carries forward Unix rwx mode bits, or the Windows DACL and basic file
attributes, but not ownership, timestamps, ACL details beyond that DACL, extended attributes,
alternate streams, sparse state, or hard-link identity. Use a fresh output path when those details
matter. A newly created single-file destination deliberately keeps the private staging security:
requested mode 0600 on Unix (or stricter after the process umask) or a protected owner-only DACL on
Windows. A newly created archive root similarly requests mode 0700 or uses an owner-only DACL; tar
file modes beneath it are still applied as described above. If an explicit archive destination is
an existing empty directory, its root instead carries forward the caller's full Unix directory
mode, including setgid/sticky policy bits, or its Windows DACL and basic attributes. This preserves
destination-root policy, not directory modes from the archive. Publishing the complete tree is
non-merging, but replacement of the empty directory is not promised as one atomic filesystem
operation.

### `receive` options

| Option | Supported values | Default | Description |
|---|---|---|---|
| `--device <d>` | ffmpeg device name/spec | Windows: required unless `--screen`; Linux: `/dev/video0`; macOS: `0` | Camera or capture-card input |
| `--format <fmt>` | ffmpeg input format | Windows: `dshow`; Linux: `v4l2`; macOS: `avfoundation` | Override the platform capture framework |
| `--screen` | flag | off | Capture this machine's display instead of a camera; takes precedence over `--device` |
| `--region <x,y,w,h>` | integer x/y; positive w/h | whole screen | With `--screen`, restrict capture to a rectangle |
| `--fps <n>` | finite, >0–120 | 10 (or `ReceiveFps`) | Capture sampling rate |
| `-o, --out <path>` | any path | decoded-name rules above | Output file or archive directory |
| `-p, --password <pw>` | any string | — | Password for an encrypted transfer; command-line exposure warning above applies |
| `--password-file <file>` | strict UTF-8 file | — | Protected-file password source; mutually exclusive with `-p` and stdin |
| `--password-stdin` | first line | — | Standard-input password source |

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
  "terminalConflicts": 0,
  "files": [
    { "fileName": "report.pdf", "fileId": "aca8f0d2a53524aa", "dataPresent": 24,
      "dataTotal": 26, "parityPresent": 0, "recoverable": false,
      "missingCount": 2, "missingTruncated": false, "missing": [3, 11] }
  ]
}
exit 3
```

There is deliberately no `restored` key on that shape. Multi-family decode preflights every family
and publishes **nothing** unless all are recoverable and internally consistent. `missing` is capped
at 256 ordinals while `missingCount` remains exact; `terminalConflicts` reports ordinals for which
two valid candidates from the same shard family disagreed. Inconsistent family metadata is a hard
error, not an erasure that can poison a session. An explicit `-o` cannot name multiple outputs, so a mixed-FileId
folder must be decoded without it or split up.

## Workflow tools: sessions, watch, verify, heatmap, calibrate

- **Sessions** (`--session s`): capture in as many sittings as you like. The owner-only v2 session
  is atomically initialized, then extended as an exclusive, CRC-framed append journal; a torn final
  record is recovered and repaired on the next save, while interior corruption fails closed.
  Conflicting same-family valid candidates persist as compact terminal erasures and are treated as missing.
  Deletion first quarantines and authenticates the exact opened object so a path-replacement race
  cannot delete a foreign file. Sessions contain raw shard payloads — plaintext for an unencrypted
  transfer — so protect them like the source.
- **Watch mode** (`decode incoming/ --watch --session s`): leave the receiver running and just
  keep dropping captures in — it decodes each as it lands and assembles automatically.
- **`verify`**: is this set complete/recoverable? Per-file data/parity counts, missing indices,
  parity-coverage status; exit 0 only when fully reassemblable, 3 when images are still missing
  (1 covers hard errors such as no decodable shards, corruption, or I/O). `--json` for scripts.
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
Comments are allowed in it, and the settings it ships with are documented inline there. Precedence: **CLI flag >
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
| `EncodeMemoryBudgetMB` | 64–1000000 | 2000 | Planning budget for resident compressed/encrypted payload bytes, retained parity/FEC buffers, and image canvases. Compression is skipped when its conservative transient peak would exceed the budget; password encryption or a payload/parity plan that cannot fit one canvas is refused with an actionable error. Remaining capacity caps parallel workers, also hard-capped at the logical core count |
| `DecodeMaxParallelism` | 0–1024 | 0 (auto: cores, capped at 24) | Upper bound on parallel image decodes. The memory planner and image count can reduce the actual pool further |
| `DecodeMemoryBudgetMB` | 64–1000000 | 4000 | Configuration limit applied independently to worker planning (~40 bytes/pixel), per-image pre-load admission (~6 bytes/pixel), path/input materialization, retained successful payloads, fusion salvage/work, watch/video lifetime state, and retained session payload/header/key state. These are conservative admission ceilings, not one subtractive pool or a hard process-working-set limit. The on-disk session journal is separately capped at 3× this byte value |
| `ReceiveFps` | >0–120 | 10 | Default frame rate for the live `receive` capture |
| `WatchPollMs` | 50–60000 | 250 | Folder poll interval (ms) for `decode --watch` |
| `ReceiveDecodeWorkers` | 0–64 | 0 (auto) | Parallel frame-decode workers for the live receiver |
| `FfmpegPath` | absolute executable path | safe absolute PATH lookup | Pin the ffmpeg executable. Relative/current/application-directory discovery is refused; the child receives a restricted PATH |
| `EncodeProfiles` | `{ "<name>": { …encode-default keys… } }` | (none) | Named encode presets selected with `--profile <name>`; each starts from `EncodeDefaults` and overrides only the keys it names |

### Tuning for a large machine

The encode budget first governs compression/encryption and fixed payload/FEC admission as described
above. Once that fixed plan and one render canvas fit, its remaining capacity can only lower the
render-worker count; raising it never takes encode above the logical core count. Decode uses the
lower of the image count, `DecodeMaxParallelism` (auto is `min(cores, 24)`), and
`floor(DecodeMemoryBudgetMB / largest-image-planning-cost)`, clamped to at least one worker. The
decode planning cost is deliberately conservative at roughly **40 bytes per source pixel**: it
includes the loaded RGB image, pooled pixels, visited/luminance/dark maps, two 8-byte-per-pixel
integral images, camera fallback, and measured overhead. It controls concurrency; allocator pools,
codec internals, GC overlap, and other process memory mean it is not a hard working-set limit.

| | Worker cap | Raisable? |
|---|---|---|
| **Encode** | the logical core count | **No.** No setting exceeds it — more workers than cores does not help CPU-bound work |
| **Decode** | `DecodeMaxParallelism`, default auto = `min(cores, 24)`, then reduced by the memory planner | **Yes**, up to 1024, but the budget must also afford it |

At the 4000 MB default, a 3840×2160 input plans at ~332 MB per worker, so even a 32-thread
machine runs about 12 batch-decode workers. A 48 MP photo plans at ~1.92 GB, so it runs about two.
To request 32 workers for 4K input you must deliberately raise both constraints; for example,
roughly 10.7 GB is required by the planning formula:

```json
{ "DecodeMaxParallelism": 32, "DecodeMemoryBudgetMB": 11000 }
```

There is also a **separate single-image gate** before pixels are loaded: approximately two RGB24
surfaces, or 6 bytes/pixel, must fit the same `DecodeMemoryBudgetMB`. It avoids charging a clean,
axis-aligned shard for the full camera fallback while still bounding one hostile image. Raising the
setting therefore increases both concurrency and the largest individually admitted image; do it
only when the machine has the headroom and the input is trusted.

Deliberately *not* configurable: anything both sides of a transfer must agree on — frame
geometry, metadata-strip layout, magic numbers, Reed-Solomon/GF(2⁸) parameters — plus the
decoder's detection heuristics. Those are protocol, not preference. Shards carrying header
flags from a newer QrShard fail with an explicit "update QrShard" error rather than decoding
wrong.

### Version compatibility

Old shards decode on new readers, always: the golden fixtures pin every released minor line
against the current decoder, and that direction is a hard commitment.

**The reverse is not, and 1.6.0 broke it.** Its metadata strip carries error correction and
declares version 4, which readers older than 1.6.0 reject outright — deliberately, since the
version nibble is the format's capability field and guessing at an unknown one is how a decoder
silently produces the wrong bytes. So:

> **Upgrade the receiver first, or upgrade both ends together.** A sender on 1.6.0 **or newer**
> talking to a receiver on 1.5.x produces images the receiver cannot read.

Header *flags* are a separate extensibility mechanism: a new feature need not change the metadata
layout version, but an older reader still rejects a set that uses a flag it does not know. That is
fail-safe feature negotiation, not reverse compatibility.

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
get 7 parity images in its stripe, so any 7 images in that stripe can be lost. End-to-end time is
usually dominated by *capture cadence*: at a manual ~3 s per screenshot, ~72 images is about
3–4 minutes; automated capture is faster. Treat the generated benchmark table below as the
performance source of record rather than extrapolating a fixed codec rate. Not sure what density
your setup survives? `qrshard calibrate`. Hard limits: ≤1.5 GB per single-file payload or prepared
archive; ≤100,000 archive entries; ≤128 path segments per archive entry; and ≤200,000
distinct archive path nodes on decode. Display size caps per-image resolution.

### Decode frame rate

How fast the *receiver* turns captured images back into data. Two figures per density: **one core**
(the portable per-frame cost) and a benchmark-machine **parallel probe**. Payload rate is the
parallel frame rate times the payload each frame carries; the 250 MB column is decode time alone
at that measured rate. These are controlled probe results, not a promise that every decode mode
uses that worker count.

| Resolution / density | Payload / image | Frames/s, 1 core | Frames/s, parallel* | Payload rate* | 250 MB, decode only* |
|---|---:|---:|---:|---:|---:|
| 2160² · 3 px · 4-bit *(Default)* | ~212 KB | ~52 (19 ms) | ~238 | ~50 MB/s | ~5.0 s |
| 2160² · 2 px · 6-bit *(Dense)* | ~716 KB | ~39 (25 ms) | ~218 | ~156 MB/s | ~1.6 s |
| 3840×2160 · 1 px · 6-bit *(Max4K)* | ~4.9 MB | ~14 (70 ms) | ~81 | ~397 MB/s | ~0.6 s |
| 3840×2160 · 1 px · 8-bit | ~6.5 MB | ~13 (77 ms) | ~76 | ~498 MB/s | ~0.5 s |

\*Parallel figures are from the [benchmark machine](#benchmark-snapshot) (32 logical cores) and
scale down on fewer cores. Batch folder decode is capped by cores, `DecodeMaxParallelism`, image
count, and the ~40-byte/pixel memory planner; with the current 4000 MB default it plans about 12
workers for 4K input, not the auto cap of 24. File-recording decode is sequential so that early
stop and frame ordering stay deterministic. Live receive defaults to 2–4 workers depending on
core count (or `ReceiveDecodeWorkers`). Reproduce the probe in `tests/QrShard.Benchmarks` with
`dotnet run -c Release -- --fps-probe`. These persisted measurements are from v1.6.1; they are a
baseline, not timings asserted for an unmeasured later revision.

Notice the frame rate *falls* with density while the payload rate *rises*: a 4K frame decodes
more slowly but carries much more data, so dense, clean captures can still move more bytes per
second and need fewer images — the same reason Max4K wins the [transfer charts](#charts). On the
benchmark machine the default 2 fps slideshow cadence sits below the measured one-core clean-frame
rates. Decode can nevertheless become the limit on slower machines, camera/rectification paths,
very large photos, aggressive slideshow intervals, or memory-throttled pools, so measure the real
capture chain rather than treating the table as a universal ceiling.

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
that id lives in each shard's data-cell header.

## Image formats

Shards can be written in any of six lossless container formats (`-f`); the container is
transport-only — decoding, ECC, and recovery are identical through all of them. The following is
a historical benchmark-machine snapshot for a 100 MB transfer at the default density, useful for
relative format trade-offs rather than as a current-checkout performance guarantee:

| Format | Encode | Decode | Disk | Notes |
|---|---:|---:|---:|---|
| `png` (default) | 3.0 s | 2.0 s | 365 MB | built-in fast writer *and reader*; best balance |
| `qoi` | 2.6 s | 2.2 s | 1.5 GB | simplest codec, very fast |
| `bmp` | 4.2 s | 2.5 s | 6.6 GB | uncompressed; disk-write bound |
| `tga` | 3.2 s | 3.5 s | 2.4 GB | RLE |
| `tiff` | 6.2 s | 2.9 s | 973 MB | deflate level 1 |
| `webp` | 21 s | 5.2 s | 194 MB | lossless mode; smallest, slowest |

GIF is deliberately unsupported as a **shard output format**: its 256-color palette cannot hold
the 8-bit cell palette plus the frame and strip colors. Animated GIF is accepted as a recording
input. JPEG and other lossy formats are not shard outputs — the format requires bit-exact source
pixels — though captured JPEG images can decode when their damage remains within the ECC budget.

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

   Chase tries the classifier's runner-up values. Few ambiguous cells get an exhaustive subset
   search; many get an all-flipped trial — the codeword the classifier would have produced had
   every coin-flip landed the other way, which is what systematic blur across a region actually
   looks like. When most but not all of them were misread, flipping the ones already right can
   overshoot the correction bound, so at parity 16 and above a further subset search un-flips up
   to six positions to get back under it. That extra search is deliberately not offered below
   parity 16: with fewer syndromes to spare it returns more wrong answers than right ones.

   When several trial patterns verify — which is common past the errors-only bound — the one
   chosen is the one Reed-Solomon spent the fewest corrections on, not the first to be reached.
   A candidate that used less of its budget sits deeper inside its decoding sphere and is much
   less likely to be a miscorrection; at parity 8 that choice is right 60% of the time against
   43% for the first hit, with nothing lost at any parity.
3. **Multi-capture fusion**: several photos of the same shard that each fail on their own are
   combined — per-codeword selection with a majority vote from three captures up; with exactly
   two, the spatial clusters where the captures disagree are hypothesis-tested (glare moves
   between shots; the payload CRC gates the answer).
4. **Cross-shard parity** (`--recovery`) or **fountain coding** (`--fountain`): whole missing
   images are rebuilt without recapture. Parity is a systematic Cauchy erasure code — any *S*
   of the stripe's *S+P* images reconstruct it. Fountain frames are random linear combinations
   with header-derivable coefficients — any full-rank subset of captured frames solves the
   stripe, without Cauchy's 255-row ceiling; the configured `-F` percentage determines how many
   coded frames this encoder emits.
5. **Detection**: CRC-32 per payload, CRC-32-protected headers, and a SHA-256 of the whole file
   carried in every image and verified after reassembly — a successful decode strongly checks
   that the output matches the content declared by that shard set. This is content integrity,
   not sender authentication: an attacker who can replace the whole set can supply a new hash.
   Current encrypted payloads are additionally authenticated by the domain-separated AuthMetaV2
   AES-GCM suite. Its associated data binds original size, SHA-256, exact UTF-8 filename, and the
   family-wide compression/archive/fountain semantics, so changing a name/size or flipping Archive
   fails authentication rather than redirecting or reinterpreting verified bytes. Per-image
   ordinal/payload fields, image count, recovery geometry, and transformed length remain outside
   the AAD; CRC, family-consistency, length, and final-SHA checks validate those before publication.
   Older encrypted sets retain their documented legacy AAD behavior.
6. **Structural redundancy**: the self-describing metadata strip and the palette calibration
   strip are duplicated top and bottom, so an overlay across either edge cannot brick an image.
   When both palette strips are healthy but differ (vertical illumination gradients — screen
   falloff, room light), the decoder *interpolates the reference palette per grid row* between
   them instead of picking one. Both copies sit at the same x as each other, though, so one
   narrow vertical mark can reach the same place in both — which is why neither strip relies on
   the duplication alone. The metadata strip carries Reed-Solomon parity of its own (metadata
   version 4), correcting a burst across two of its sixteen symbols. The palette strips are
   protected from both directions: selection excludes a copy whose colors have collapsed onto
   each other before comparing distance to the theoretical palette, so one damaged copy cannot
   displace a healthy but strongly gain-shifted one. A mark across **one** copy is also rejected
   from gradient interpolation rather than mistaken for illumination — a shadow displaces one
   entry, where lighting moves them all together. If neither copy provides separable colors, the
   decoder falls back to the theoretical palette, which SPEC §3 derives from the bit depth without
   reading anything from the image.

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

This persisted snapshot was measured on the **v1.6.1** code. It remains useful as a
hardware-specific baseline, not a claim that an unmeasured later revision has identical timings;
use the reproduction commands below for the current checkout and target machine.

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
**Max4K** = 3840x2160, 1 px, 6 bits; **Max4K-R10** = Max4K + 10% parity images. The generated
[All measurements](#all-measurements) table is the single source of benchmark values; the charts
are exported from the same persisted result set. A relative **perf gate** also races base and head
on the same 30 MB round trip and fails a >30% median regression.

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

> **Read these as order-of-magnitude, not as a stopwatch.** The suite runs one warmup and three
> iterations per case under the Monitoring strategy, which is the right trade for operations this
> long and IO-heavy, but it leaves wide confidence intervals on the smaller rows — the 99.9% CI on
> 10 MB/Default is several times its own mean. Differences under about 20% on any single row are
> below the instrument's resolution here. The large-file rows, where each iteration runs for
> seconds, are the trustworthy ones.
>
> **Do not compare these figures against an older revision of this table.** Every row above was
> measured in ONE sitting on an otherwise-idle machine (sustained load under 3% of 32 logical
> cores, verified before and after). Earlier tables were taken in different sittings, and the
> machine state between them moves the numbers more than most code changes do: re-measuring
> **v1.6.0 itself** on the machine and day these figures come from gave 3.91 s for 1 GB/Max4K
> decode, against the 2.42 s its own published table claimed — and against 4.02 s for 1.6.1 here,
> which is inside the error bars. Cross-session deltas in this table are machine drift, not
> progress or regression. To compare two revisions, build both and measure them back to back.

<!-- BENCH:TABLE:START -->
| Size | Preset | Images | Encode | Decode | Codec MB/s | Est. manual (3 s/img) | Est. auto (0.5 s/img) |
|---|---|---:|---:|---:|---:|---:|---:|
| 1KB | Default | 1 | 12.8 ms | 46.6 ms | 0.016 | 3.06 s | 559.4 ms |
| 1KB | Dense | 1 | 15.6 ms | 51.3 ms | 0.015 | 3.07 s | 566.9 ms |
| 1KB | Max4K | 1 | 62.9 ms | 117 ms | 0.005 | 3.18 s | 679.9 ms |
| 1KB | Max4K-R10 | 1+1p | 103.1 ms | 128.7 ms | 0.004 | 6.23 s | 1.23 s |
| 10KB | Default | 1 | 13.6 ms | 49.8 ms | 0.154 | 3.06 s | 563.3 ms |
| 10KB | Dense | 1 | 17.4 ms | 52 ms | 0.141 | 3.07 s | 569.3 ms |
| 10KB | Max4K | 1 | 81 ms | 95 ms | 0.055 | 3.18 s | 676 ms |
| 10KB | Max4K-R10 | 1+1p | 87.2 ms | 130.4 ms | 0.045 | 6.22 s | 1.22 s |
| 100KB | Default | 1 | 50.4 ms | 46.2 ms | 1 | 3.1 s | 596.6 ms |
| 100KB | Dense | 1 | 30.9 ms | 57.7 ms | 1.1 | 3.09 s | 588.6 ms |
| 100KB | Max4K | 1 | 77.3 ms | 98.6 ms | 0.555 | 3.18 s | 675.9 ms |
| 100KB | Max4K-R10 | 1+1p | 91 ms | 128.4 ms | 0.445 | 6.22 s | 1.22 s |
| 500KB | Default | 3 | 64.1 ms | 50.1 ms | 4.3 | 9.11 s | 1.61 s |
| 500KB | Dense | 1 | 107 ms | 51.9 ms | 3.1 | 3.16 s | 659 ms |
| 500KB | Max4K | 1 | 78.2 ms | 106.7 ms | 2.6 | 3.18 s | 685 ms |
| 500KB | Max4K-R10 | 1+1p | 91.3 ms | 130.3 ms | 2.2 | 6.22 s | 1.22 s |
| 1MB | Default | 5 | 66.7 ms | 42.4 ms | 9.2 | 15.11 s | 2.61 s |
| 1MB | Dense | 2 | 124.1 ms | 56.9 ms | 5.5 | 6.18 s | 1.18 s |
| 1MB | Max4K | 1 | 76.8 ms | 109.2 ms | 5.4 | 3.19 s | 686 ms |
| 1MB | Max4K-R10 | 1+1p | 93.7 ms | 130 ms | 4.5 | 6.22 s | 1.22 s |
| 10MB | Default | 50 | 275.5 ms | 256.7 ms | 18.8 | 2.5 min | 25.53 s |
| 10MB | Dense | 15 | 212.1 ms | 77.8 ms | 34.5 | 45.29 s | 7.79 s |
| 10MB | Max4K | 3 | 98.4 ms | 151.2 ms | 40.1 | 9.25 s | 1.75 s |
| 10MB | Max4K-R10 | 3+1p | 149.4 ms | 156.8 ms | 32.7 | 12.31 s | 2.31 s |
| 100MB | Default | 495 | 2.25 s | 910.2 ms | 31.6 | 24.8 min | 4.2 min |
| 100MB | Dense | 147 | 1.43 s | 462.7 ms | 53 | 7.4 min | 1.3 min |
| 100MB | Max4K | 22 | 370.3 ms | 505.1 ms | 114 | 1.1 min | 11.88 s |
| 100MB | Max4K-R10 | 22+3p | 342.7 ms | 551.9 ms | 112 | 1.3 min | 13.39 s |
| 250MB | Default | 1238 | 4.67 s | 2.2 s | 36.4 | 1.03 h | 10.4 min |
| 250MB | Dense | 366 | 3.55 s | 1.03 s | 54.6 | 18.4 min | 3.1 min |
| 250MB | Max4K | 54 | 597.7 ms | 1.09 s | 148 | 2.7 min | 28.69 s |
| 250MB | Max4K-R10 | 54+6p | 661.3 ms | 1.17 s | 137 | 3 min | 31.83 s |
| 500MB | Default | 2475 | 8.78 s | 4.85 s | 36.7 | 2.07 h | 20.9 min |
| 500MB | Dense | 732 | 6.56 s | 2.05 s | 58.1 | 36.7 min | 6.2 min |
| 500MB | Max4K | 108 | 1.12 s | 1.96 s | 163 | 5.5 min | 57.08 s |
| 500MB | Max4K-R10 | 108+11p | 1.26 s | 2.1 s | 149 | 6 min | 1 min |
| 1GB | Default | 5068 | 16.15 s | 8.92 s | 40.8 | 4.23 h | 42.7 min |
| 1GB | Dense | 1499 | 11.15 s | 4.07 s | 67.2 | 1.25 h | 12.7 min |
| 1GB | Max4K | 220 | 2.3 s | 4.02 s | 162 | 11.1 min | 1.9 min |
| 1GB | Max4K-R10 | 220+22p | 2.92 s | 4.31 s | 142 | 12.2 min | 2.1 min |
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
│ │ │ metadata strip (128 modules) │ │ │  ← geometry + density + ECC level; CRC-16 + RS
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
- **Custom fast PNG writer AND reader** ([FastPng.cs](src/QrShard.Core/FastPng.cs),
  [FastPngReader.cs](src/QrShard.Core/FastPngReader.cs)): the writer streams one IDAT straight from
  the render buffer (row-blit rendering, Up filter — or raw *stored* deflate blocks for
  incompressible 1 px cells); the reader handles the truecolor subset every screenshot tool
  emits, ~2x faster than a general decoder, falling back to ImageSharp for anything else.
- **Bounded copies where the transform permits**: large incompressible inputs are memory-mapped
  and read per chunk by encode workers; reassembly streams chunks through decompression to a
  staged file with incremental SHA-256. Compressible encoding materializes the Brotli input,
  growing output stream, and final result transiently, so it is attempted only when a conservative
  four-input-length peak fits `EncodeMemoryBudgetMB`; otherwise compression is safely skipped.
  Encryption needs a contiguous whole-message buffer because AES-GCM authenticates before releasing
  plaintext; QrShard encrypts/decrypts that blob in place and refuses a password-protected payload
  that cannot fit the configured budget. Retained payload and parity bytes are subtracted before
  choosing image-worker parallelism.
- **Table-driven Reed-Solomon with SIMD on both paths**: 16 codewords per `Vector128` lane for
  the decode-side syndrome scan *and* the encode-side LFSR (nibble-shuffle product tables);
  clean codewords skip the scalar decoder entirely. Cross-shard parity and fountain coding use
  SIMD GF(2⁸) multiply-accumulate. Grid sampling uses precomputed per-row/per-column
  coordinate tables — per-cell work is array lookups, not floating-point math.
- **GC discipline**: server GC; per-worker scratch buffers everywhere; exact-size buffers; the
  camera refinement path evaluates its interpolation fields with zero per-pixel allocations.

### Image library choice

Decode must parse arbitrary screenshots from unknown tools — that needs a mature fallback:
**ImageSharp** (pure managed, cross-platform; pinned to v4.0.0 under Apache-2.0 for this
MIT-licensed open-source project). The hot paths (PNG in both directions) are hand-rolled; everything else goes
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

Requires the exact .NET SDK **10.0.302** enforced by `global.json`. Run `dotnet build -c Release`
at the solution root. `./publish.ps1` and `bash ./publish.sh` create self-contained JIT single-file
builds; tagged Native-AOT assets are built by the release workflow instead.

The repository's build, security-analysis, dependency, release-candidate, and scheduled assurance
workflows are:

| Workflow | What it guards |
|---|---|
| **CI** | Build + the full suite on windows-2025, ubuntu-22.04, ubuntu-24.04-arm and macos-15 — every platform a release binary is published for. Each job renders totals, a per-class breakdown and the slowest tests into the run summary |
| **CodeQL** | Production C# and GitHub Actions analysis on every PR/main push, weekly, and on manual dispatch; C# uses the default remote model plus hostile local-file contents (the precise trust boundary is documented in `SECURITY.md`) |
| **Dependency Review** | Rejects a pull request that introduces a dependency with a low-or-higher known vulnerability |
| **Interop** | Four encoders x four decoders: shards encoded on each OS/arch must decode on every other, forcing parity reconstruction (where the x64 and arm64 GF(2⁸) paths could disagree) and covering the encrypted path |
| **Package** | Packs both NuGet packages and consumes them from *outside* the repo — compiles the readme's own code sample against the packed package, round-trips through the public API, and installs the dotnet tool |
| **Perf gate** | Base and head builds race the same 30 MB round trip; a >30% median regression fails |
| **Release** | PR/manual runs exercise the complete read-only Native-AOT/package/SBOM candidate path; canonical `v*` tags alone can enter the protected promotion jobs |
| **Dependency Submission** | Restores the locked graph and submits it to GitHub on `main` and manual runs |
| **Fuzz** | Weekly, 20 000-seed deep run of the structure-aware fuzz suite: PNG/image decode, metadata/header parsing, crafted recovery geometry, sessions, encrypted blobs, and clipboard DIBs (the image-sized noise target uses a bounded subset) |

The ImageSharp package runs a compile-time license-validation target. GitHub does not expose
ordinary Actions secrets to fork or Dependabot pull requests, so the CI key is stored separately as
an encrypted Dependabot secret as well as an Actions secret; it is never committed. Dependabot
alerts, security updates, and weekly NuGet/.NET-SDK/GitHub-Actions update proposals are enabled. Bot PRs are
not auto-merged. Fork changes are not merged directly: a maintainer must reproduce them on a trusted
repository branch and all required checks must pass. A red no-secret fork run is not evidence that
the code itself failed. Same-repository branches are a maintainer trust boundary because candidate
MSBuild and package steps receive the encrypted ImageSharp build key; do not grant branch-write
access to an untrusted contributor.

`main` is covered by an active repository ruleset: changes require a pull request, open review
threads must be resolved, and branch deletion and non-fast-forward updates are blocked. The rule
requires all 15 CI, dependency-audit, interop, package-consumer, and performance contexts from the
GitHub Actions app to pass against the latest `main`. It requires no approving review because this
is presently a sole-maintainer repository, and it has no bypass actor. A separate active tag
ruleset blocks update or deletion of every `v*` release tag, also without a bypass actor, while
still allowing a new tag to be created. Repository Actions policy permits GitHub-owned actions plus
the explicitly used `NuGet/login`, `dorny/test-reporter`, and
`advanced-security/component-detection-dependency-submission-action`, and requires every `uses:`
reference to be a full commit SHA. These controls, the protected release environment, and release immutability are GitHub-side
state rather than immutable files; re-verify them after a repository transfer or settings change.

Tagged releases are stable-only: preflight requires canonical `vMAJOR.MINOR.PATCH` tags (so NuGet
normalization cannot disguise an occupied version), requires the tag commit to be on `main`,
requires the tag version to match both project versions, and fails closed unless the live `release` environment has
a required reviewer and exactly one `v*` deployment tag policy, and the repository has the exact
tag update/deletion rules and ref pattern described above. GitHub deliberately hides a ruleset's
bypass actors from the read-only workflow token, so the current no-bypass setting remains a
GitHub-side governance check to re-verify manually; the workflow does not pretend otherwise. The
remote tag is peeled and compared with the event commit again before attestation, draft creation,
and publication. Runs for the same tag are serialized without cancelling the earlier run.

Four read-only matrix jobs on windows-2025, ubuntu-22.04, ubuntu-24.04-arm and macos-15 use the exact
.NET SDK 10.0.302 to test and Native-AOT publish win-x64, linux-x64, linux-arm64 and osx-arm64, add
redistribution notices, and smoke-test the **exact** `QrShard[.exe]` bytes (tagged version,
self-test, and a real parity-recovery round trip). Each of those same-host matrix jobs then pins
Microsoft.Sbom.DotNetTool 4.1.5 and generates the archive's SPDX 2.2 document from the exact
RID-aware publish graph. A separate read-only job packs each NuGet package once and consumer-tests
those exact packages; a clean package-SBOM job generates the two ordinary framework-dependent
graphs. All six SBOMs validate the staged artifact hash and reject stale, test, benchmark, and
SBOM-tool components. The four binary manifests also reject wrong-RID graphs; the package
manifests reject Native-AOT contamination.

The committed NuGet lock files describe the portable framework-dependent graph used by ordinary
builds, tests, packaging, and dependency submission. Each Native-AOT matrix restore writes its
mutually exclusive RID graph to an ignored `obj/aot/<rid>/packages.lock.json`, preventing a local or
CI AOT publish from contaminating those committed portable lock files; the per-RID SBOM checks the
exact runtime and compiler-pack versions instead.

Only after those jobs pass does `create-draft` wait at the protected `release` environment. Its
configured reviewer must explicitly approve the run; self-review is permitted because the
repository currently has one maintainer, while administrator bypass is disabled. That
artifact-only, no-checkout job creates signed SLSA provenance for every release file, attaches each
of the six artifact-specific signed SBOM predicates, and creates one complete draft containing the
four archives, both packages, six SBOM documents, and `SHA256SUMS`. A downstream no-checkout NuGet
OIDC job first validates both exact package names and their bounded ZIP structure, then performs a
read-only two-registry preflight. Any existing NuGet.org copy must have a valid repository signature
and be semantically identical apart from that signature; any GitHub Packages copy must be
byte-identical. Only after **all** existing copies pass does it publish missing immutable versions,
poll and reconcile ambiguous responses, and mirror the tested bytes. A final no-checkout job makes the
draft public and immutable. No repository code is checked out or built in a write-enabled job.

### Release recovery: partial NuGet publication

If a package source accepts one package and a later write or response fails, leave the tag and draft
in place and rerun the failed workflow jobs. The idempotent publication job discovers the official
V3 package endpoints, validates every present copy across both registries before any new write,
pushes only absent versions, and then downloads and authenticates the result. It never relies on
`--skip-duplicate`, which would not prove byte identity. Never rebuild or retag the same version.

Draft creation is likewise reconciled: a rerun accepts only an existing **draft**, rejects any
unexpected asset, downloads every present asset, and requires exact byte equality before uploading
missing assets. It never deletes an uncertain draft automatically. If provenance is unclear,
inspect it manually:

```sh
gh release view "$tag" --json isDraft,author,assets,targetCommitish,url
```

If it is not the expected workflow-owned draft targeting the intended commit, leave it untouched
and investigate. Do not delete a draft merely to make a rerun pass.

Recover the partial package publication as follows:

1. Leave the tag and GitHub release **draft** in place and use **Re-run failed jobs** on the same run.
2. The retained, already consumer-tested artifact is reconciled against each registry; present
   copies must match and missing copies alone are pushed.
3. The final job downloads the draft again, checks its exact inventory and bytes against the
   workflow artifacts plus `SHA256SUMS`, then publishes it. If any occupied version or draft asset
   differs, automation stops: leave that version occupied and release a new version after review.

For releases produced by this workflow beginning with v1.6.2, GitHub stores signed SLSA provenance
for every exact GitHub Release file (including `SHA256SUMS`) and an artifact-specific signed
SPDX 2.2 predicate for each archive and package. Release immutability then prevents
publication-time assets or the tag from being replaced. These controls authenticate GitHub
workflow provenance, not operating-system publisher identity: Windows remains unsigned by
Authenticode, and the workflow does not verify a Developer ID signature or notarization for macOS.
The plaintext `SHA256SUMS` file is not itself a detached platform signature; verify its attestation
rather than trusting the file in isolation.

ImageSharp is pinned to **4.0.0** and is used by this MIT-licensed open-source project under
**Apache-2.0**; copyright (c) Six Labors. The Apache-2.0 text ships in release archives and the
global-tool package. Repository/CI build-validation keys are never committed; when the package's
build target requests one, use your own gitignored `sixlabors.lic` or the `SixLaborsLicenseKey`
environment variable. This is a technical notice, not legal advice.

- `dotnet test` — the xUnit suite. Covers the codec math (CRC vectors, GF(2⁸) field
  laws, Reed-Solomon incl. errors-and-erasures, interleaving, Cauchy and fountain erasure
  codes), round trips across every density/ECC/format/flag combination, simulated screenshots
  and camera photos, non-truecolor capture shapes, video recordings (duplicates, torn frames,
  early stop, camera video with pose drift), encryption, archives, sessions, watch mode,
  fusion, calibration, randomized robustness fuzzing of the parser surfaces listed above, and
  the CLI. `EnvironmentAssumptionTests` additionally exercises Turkish-I command dispatch,
  invariant settings/resolution parsing under hostile ambient cultures, Unicode filenames through
  the real filesystem encode/decode chain, and a forced single-worker decoder. The named-culture
  theories and the Native-AOT release binaries use full globalization data; the exact AOT smoke
  tests include canonically equivalent Unicode archive-name collision rejection.
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
