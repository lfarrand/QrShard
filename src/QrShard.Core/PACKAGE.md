# QrShard.Core

QrShard.Core is the embeddable .NET codec behind
[QrShard](https://github.com/lfarrand/QrShard). It encodes files into dense, self-describing
QR-style images and decodes captured image paths or in-memory image bytes back into verified files.
A successful decode is checked against the original length and SHA-256 before output is published.

Use this package when an application owns capture, transport, storage, or UI. Use
[QrShard.Tool](https://www.nuget.org/packages/QrShard.Tool) for the complete command-line workflows,
including folder/archive creation, slideshows, video demuxing, live webcam/screen capture, watched
folders, clipboard capture, calibration, diagnostics, and JSON output.

The image format is purpose-built for high-capacity screen transfer; it is not the QR Code standard.
Core and Tool are wire-compatible in both directions.

## Install

```console
dotnet add package QrShard.Core
```

The package targets and requires **.NET 10**. It is pure managed code, has no ffmpeg dependency,
and is analyzer-marked Native AOT / trimming compatible (`IsAotCompatible`).

## Quick start

```csharp
using QrShard;

var codec = new QrShardCodec();

var report = codec.EncodeFile(
    "report.pdf",
    "report.shards",
    new QrShardEncodeOptions
    {
        RecoveryPercent = 15,
        EccParity = 16,
        CameraMode = false,
        Password = null,
    });

Console.WriteLine($"Wrote {report.ImageCount} images at {report.Width}x{report.Height}");

Directory.CreateDirectory("restored");
IReadOnlyList<QrShardDecodedFile> restored = codec.DecodeImages(
    Directory.GetFiles("captures"),
    outputPath: "restored",
    password: null);

var session = new QrShardDecodeSession(password: null, decodeMemoryBudgetMB: 512);
foreach (string imagePath in Directory.EnumerateFiles("incoming"))
{
    QrShardAddResult added = session.AddImage(imagePath);
    if (!added.Accepted)
        Console.Error.WriteLine($"{imagePath}: {added.Error}");
}

foreach (QrShardFileStatus status in session.Status())
    Console.WriteLine($"{status.FileName}: {status.MissingImageCount} missing");

if (session.IsComplete)
    session.Assemble("restored");
```

The namespace is `QrShard`; the NuGet package ID is `QrShard.Core`.

## Public API

| Type/member | Purpose |
|---|---|
| `QrShardCodec` | Reusable, thread-safe one-shot encode/decode facade |
| `EncodeFile(...)` | Encode one file as PNG shard images and return geometry/file details |
| `DecodeImages(...)` | Decode image paths in any order, repair/recover them, and publish verified output |
| `QrShardEncodeOptions` | Geometry, colour density, ECC, recovery/fountain, camera, encryption, compression, and interleave settings |
| `QrShardEncodeReport` | Image counts, capacity, dimensions, and written paths |
| `QrShardDecodedFile` | Original name, resolved output path, and verified length |
| `QrShardDecodeSession` | Single-consumer incremental decoder for image files or encoded image bytes |
| `QrShardFileStatus` | Per-file data/parity counts, exact missing count, bounded missing-index sample, and recoverability |
| `QrShardAddResult` | Whether an image was accepted, new, duplicate, invalid, conflicting, or resource-refused |
| `QrShardDecodeException` | Actionable failure from decode or assembly |

`QrShardCodec` instances are thread-safe and reusable. `QrShardDecodeSession` is deliberately not
thread-safe; feed it from one consumer or provide external synchronisation.

## Encoding options

`QrShardEncodeOptions` exposes the stable codec settings:

| Property | Default | Supported values/purpose |
|---|---:|---|
| `Width`, `Height` | 2160 | 700–16384 pixels per side |
| `CellPx` | 3 | 1–64; smaller cells increase density and demand cleaner captures |
| `BitsPerCell` | 4 | 1–12 bits; 13–16 stay illegal. 1–8 declare metadata version 4, 9–10 version 5, and 11–12 version 6 |
| `EccParity` | 16 | Even, 0–64 Reed-Solomon parity bytes per 255-byte codeword |
| `RecoveryPercent` | 0 | 0–100% extra Cauchy parity images for whole-image loss |
| `FountainPercent` | 0 | 0–1000% random-linear coded frames; mutually exclusive with recovery parity |
| `CameraMode` | `false` | Add finder patterns for rotation/perspective-corrected photos |
| `Password` | `null` | AES-256-GCM payload encryption |
| `Compress` | `true` | Brotli-compress when a sample indicates it is worthwhile |
| `Interleave2` | `false` | Spread vertical as well as horizontal damage; requires ECC |

Cells at 1 to 8 bits still declare metadata version 4. Their minimum channel step is at least 32. A
9-bit cell still declares metadata version 5. Its minimum channel step is 36. A 10-bit cell still
declares metadata version 5. Its split is 16×8×8 and its minimum channel step is 17. An 11-bit cell
declares metadata version 6. Its split is 16×16×8, so red and green step by 17 and blue stays at 36.
A 12-bit cell declares metadata version 6. Its split is 16×16×16 and it steps by 17 on every channel.
It is accepted only when the calibration strip covers 4096 colours. A 4K 1 px strip covers 11 bits and rejects
12. Values 13–16 stay illegal. Version 6 uses the same 128-module strip. The stream header stays
version 2, and the flag field is unchanged. On a pixel-perfect 4K grid of 3708×1876 cells at 1 px
with 16 parity bytes, the 11-bit usable payload is about 9.0 MB, about 1.10× the 10-bit figure of
about 8.1 MB.

Unlike the CLI, Core does not auto-detect a monitor or read CLI `appsettings.json`; the values in
`QrShardEncodeOptions` are explicit and deterministic. `EncodeFile` encodes one file. Applications
that need folder or multi-input archive creation should use QrShard.Tool or prepare their own file
container before encoding.

## Incremental and in-memory decode

`QrShardDecodeSession` is the embedding counterpart to the Tool's session/watch workflows:

- `AddImage(path)` decodes and retains one image file.
- `AddImageBytes(bytes, label)` accepts an encoded image already held in memory.
- `Status()` reports progress for every shard family seen.
- `IsComplete` becomes true when every family can be reconstructed, including through parity or
  fountain frames.
- `Assemble(outputPath)` stages, verifies, and publishes the restored file or files.

Duplicates are harmless. If two CRC-valid candidates disagree for the same ordinal, that ordinal
becomes a terminal erasure rather than first/last-wins. Inconsistent family metadata is rejected.
`MissingImageCount` is exact; `MissingImages` is capped at the first 256 ordinals and
`MissingImagesTruncated` indicates whether more were omitted.

The default session retention budget is 4,000 decimal MB. The overload taking
`decodeMemoryBudgetMB` accepts 1–1,000,000 MB and also derives a metadata-aware shard-count ceiling.
A resource-refused addition returns its reason in `QrShardAddResult.Error` without changing session
state.

`QrShardDecodeSession` retains failed ECC captures and runs the same multi-capture fusion as
`DecodeImages` once two or more failures of a shard are kept. **1.7.5** skipped that session
fusion; folder `DecodeImages` fused on 1.7.5 already.

## Recovery and integrity

Core provides the same image codec and reassembly protections as the Tool:

- Reed-Solomon errors-and-erasures correction within each image.
- Cross-shard Cauchy parity and fountain recovery for missing whole images.
- Multi-capture fusion for several individually failed captures of the same shard.
- Finder detection, homography, refinement, and illumination adaptation for camera-mode images.
- AES-256-GCM encryption with authenticated file/transformation metadata.
- Per-shard CRCs, family-consistency checks, exact-length validation, and final SHA-256 verification.

Shards are filename-agnostic, order-independent, and duplicate-tolerant. Captures from different
files may be supplied together; status and output are tracked per shard family.

## Output safety and limits

Ordinary files are written to private staging paths, length/SHA-256 verified, and atomically moved
into place. An existing output directory receives restored ordinary files under their sanitised
original names. Archive payloads can be decoded, but their destination must be absent or empty and
is never merged into an existing tree.

Single-file and prepared-archive payloads are limited to 1.5 GB. Archive decode accepts at most
100,000 entries, 128 path segments per entry, and 200,000 distinct path nodes. Replacing an
existing file preserves only Unix rwx mode, or the Windows DACL and basic attributes; use a fresh
path when ownership, extended metadata, or hard-link identity matters.

## Compatibility and release integrity

The current decoder is tested against fixtures from every released minor wire-format line. Older
receivers deliberately reject unknown metadata versions and feature flags, so upgrade the receiver
first or upgrade both ends together.

Beginning with v1.7.0, immutable GitHub releases include signed SLSA provenance and an
artifact-specific SPDX 2.2 SBOM attestation for the exact pre-publication package. NuGet.org then
repository-signs the uploaded package. Verification instructions are in the
[repository README](https://github.com/lfarrand/QrShard#verifying-a-v170-or-later-tagged-release).

Full documentation: <https://github.com/lfarrand/QrShard>

Wire-format specification: <https://github.com/lfarrand/QrShard/blob/main/SPEC.md>

## License

QrShard.Core is MIT licensed and uses SixLabors.ImageSharp 4.1.2 under Apache-2.0 for this
open-source project. ImageSharp remains a normal NuGet dependency rather than a bundled assembly.
The QrShard MIT license is included in the package.
