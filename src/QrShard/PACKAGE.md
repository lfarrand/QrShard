# QrShard.Tool

QrShard.Tool transfers files and folders between machines **through the screen**. It encodes data
into dense, self-describing QR-style images and restores it from screenshots, photos, recordings,
the Windows clipboard, a live camera, or a captured display. Every completed restore is checked
against the original length and SHA-256 before it is published.

Use this package when you want the `qrshard` command and complete sender/receiver workflows. For an
embeddable .NET API that works with image files or in-memory image bytes, use
[QrShard.Core](https://www.nuget.org/packages/QrShard.Core).

The image format is purpose-built for high-capacity screen transfer; it is not the QR Code standard.

## Install

```console
dotnet tool install --global QrShard.Tool
```

Update an existing installation with:

```console
dotnet tool update --global QrShard.Tool
```

The tool targets and requires **.NET 10**. Tagged
[GitHub releases](https://github.com/lfarrand/QrShard/releases) also provide self-contained
Native-AOT archives for Windows x64, Linux x64, Linux ARM64, and macOS ARM64.

Video-container decoding and live capture require [ffmpeg](https://ffmpeg.org) on a trusted absolute
`PATH` entry, or an absolute `FfmpegPath` in `appsettings.json`. Animated PNG, GIF, and WebP
recordings decode natively.

## Quick start

On the sending machine:

```console
qrshard encode report.pdf
```

Display the generated images at 100% zoom. On the receiving machine, place captures in a folder and
run:

```console
qrshard decode captures --out report.pdf
```

Captures may be renamed, reordered, or duplicated. Damaged cells are repaired when possible, and
whole missing images can be reconstructed when recovery images were generated.

## Capture workflows

| Workflow | Sender | Receiver |
|---|---|---|
| Screenshots | `qrshard encode file.bin` | `qrshard decode captures` |
| Browser slideshow | `qrshard send file.bin` | Record one cycle, then `qrshard decode recording.mp4` |
| Phone photos/video | add `--camera` | Decode photos or the phone recording |
| Live webcam/capture card | `qrshard send file.bin` | `qrshard receive --device "Integrated Camera"` |
| Capture this computer's display | show the slideshow in an RDP/VM window | `qrshard receive --screen --region x,y,w,h` |
| Windows clipboard | display individual shards | `qrshard decode --clipboard --session transfer.qrsession` |
| Captures arriving in a folder | ordinary encode/send | `qrshard decode incoming --watch --session transfer.qrsession` |

`send` is the one-step form of `encode --video --open`. HTML slideshows are relative manifests, so
keep `slideshow.html` beside its shard images and generated sidecars. `--slideshow apng` creates one
animated file but refuses sets above 256 MiB of decoded RGB frame data.

If recording decode remains incomplete after automatic higher-frame-rate retries, QrShard preserves
sampled BMP frames in the logged temporary directory for inspection or manual decoding.

## What the tool can do

- Encode one file directly, one folder, or multiple files/folders as a portable archive.
- Write lossless PNG, BMP, TGA, QOI, WebP, or TIFF shard images.
- Auto-size images to the sender's primary monitor, use named profiles, or accept exact geometry.
- Create HTML or APNG slideshows for screen-recording transfers.
- Decode ordinary captures, animated images, common video containers, webcam/capture-card streams,
  screen regions, watched folders, and Windows clipboard images.
- Resume image-based transfers across sittings with an owner-only session journal.
- Emit machine-readable JSON for encode, decode, verify, and info workflows.
- Preview image counts and geometry with `--dry-run` before rendering a large transfer.
- Diagnose captures with ECC-damage and classification-quality heatmaps, including failed images.
- Generate and analyse calibration probes for the real display/camera path.
- Run a built-in self-test or test a chosen file and settings through simulated capture damage.

Run `qrshard --help` for the complete command and option reference.

## Resilience and security

- **Reed-Solomon errors-and-erasures correction** repairs damaged cells within each image.
- **Cross-shard Cauchy parity** (`--recovery`) rebuilds whole missing images.
- **Fountain coding** (`--fountain`) creates random-linear video frames; any sufficient full-rank
  subset reconstructs each stripe.
- **Multi-capture fusion** combines several individually failed photos of the same shard.
- **Camera mode** adds finder patterns, rotation/perspective rectification, illumination correction,
  pose caching, blur rejection, and temporal averaging for photos and handheld video.
- **Permuted interleaving** (`--interleave2`) spreads vertical as well as horizontal damage across
  codewords.
- **AES-256-GCM** protects payloads when a password is supplied. Prefer `--password-file` or
  `--password-stdin`; command-line passwords may be exposed in shell history and process listings.
- Per-shard CRCs, family-consistency checks, exact-length checks, and final SHA-256 verification
  prevent damaged or mixed captures from being silently published. These checks provide content
  integrity, not sender identity.

## Files, folders, and safe output

Folder and multi-input transfers use a deliberately portable archive subset. Regular files,
directories, empty directories, and Unix regular-file rwx bits are supported. Links are not
followed; unsafe or platform-aliasing paths are rejected.

Single-file and prepared-archive payloads are limited to 1.5 GB. Archives are limited to 100,000
entries, 128 path segments per entry, and 200,000 distinct path nodes during decode.

Restored files are staged privately, checked, and atomically moved into place. Passing an existing
directory to `--out` places ordinary restored files inside it under sanitised original names.
Archives are staged as a complete tree and are never merged into a populated destination; the
destination must be absent or empty.

## Capacity and configuration

At the robust default (2160 px square, 3 px cells, 4 bits per cell), capacity is approximately
212 KB per image after default ECC. Pixel-perfect 4K captures can reach about 4.9 MB per image with
the Max4K profile and about 6.5 MB at 8-bit density. Camera mode deliberately trades density for
photo tolerance.

An optional `appsettings.json` controls encode defaults, named profiles, PNG/Brotli compression,
encode/decode memory budgets, decode parallelism, live-receiver settings, watch polling, and the
absolute ffmpeg path. CLI flags override configuration values. See the
[full configuration reference](https://github.com/lfarrand/QrShard#configuration-appsettingsjson).

## Compatibility and release integrity

The current decoder is tested against fixtures from every released minor wire-format line. Upgrade
the receiver first, or upgrade both ends together: older receivers deliberately reject features or
metadata versions they do not understand.

Beginning with v1.7.0, immutable GitHub releases include SHA-256 sums, signed SLSA provenance, and
artifact-specific SPDX 2.2 SBOM attestations. NuGet.org repository-signs the uploaded package;
verification details are in the
[repository README](https://github.com/lfarrand/QrShard#verifying-a-v170-or-later-tagged-release).
The package is also mirrored to GitHub Packages, but NuGet.org is the supported install source.

Full documentation: <https://github.com/lfarrand/QrShard>

Wire-format specification: <https://github.com/lfarrand/QrShard/blob/main/SPEC.md>

## License

QrShard is MIT licensed. QrShard.Tool uses SixLabors.ImageSharp 4.0.0 under Apache-2.0 for this
open-source project. The package carries the project license and reviewed redistribution notices
beside its bundled dependencies.
