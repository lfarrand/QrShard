# QrShard.Core

The embeddable codec behind [QrShard](https://github.com/lfarrand/QrShard): encode any file into
dense, QR-style images and decode captured image files or in-memory image bytes — screenshots,
photos, or frames extracted from recordings/cameras — back into the original file, **bit-for-bit,
verified by SHA-256**. Direct video-file demuxing and live webcam/screen capture belong to the
`QrShard.Tool` CLI, not this library's public API.

The image format is custom (not QR-standard) and tuned for screen-to-screenshot transfer. Because
a screenshot is a lossless pixel copy, each image carries far more than a real QR code: from
~212 KB per image at the robust default, to ~4.9 MB with the Max4K profile, and up to ~6.5 MB at
8-bit density on a 4K display.

## Install

```
dotnet add package QrShard.Core
```

The package targets and requires **.NET 10**.

Each release is also mirrored to GitHub Packages, but **nuget.org is the supported install
source** — GitHub Packages requires an access token with `read:packages` to install from, even
for public repositories.

Beginning with v1.6.2, the exact pre-publication `.nupkg` attached to GitHub Releases has signed
SLSA provenance and an SPDX 2.2 SBOM attestation; verification instructions are in the repository
README. Older releases predate these controls. NuGet.org then repository-signs the uploaded
package, changing its bytes. NuGet clients can verify that separate repository signature, subject
to platform and client policy.

## Use

```csharp
using QrShard;

var codec = new QrShardCodec();

// Encode a file into shard images.
var report = codec.EncodeFile("holiday-photos.zip", "out-dir");
Console.WriteLine($"{report.ImageCount} image(s)");

// Decode captures back into the original file.
codec.DecodeImages(Directory.GetFiles("captures"), "holiday-photos.zip");
```

The namespace is `QrShard` (the package *id* is `QrShard.Core`), and `QrShardCodec` has instance
methods — construct it once and reuse it.

For capture that arrives over time, `QrShardDecodeSession` decodes incrementally: feed images
(paths or in-memory bytes) as they land, ask which are still missing, and assemble the moment the
set becomes recoverable.

Shards are order-independent, duplicate-tolerant and filename-agnostic, and shards belonging to
different files can share a folder without being confused for one another.

`DecodeImages` and `QrShardDecodeSession.Assemble` stage output and verify exact length and
SHA-256. A single-file result is then atomically moved into place. An archive output directory must
be absent or empty and is never merged; its complete staged tree is published only after every
entry succeeds. Replacing an existing file carries forward only its nine Unix rwx bits, or its
Windows DACL and basic attributes. An existing empty archive destination instead carries its full
Unix directory mode (including setgid/sticky policy bits), or its Windows DACL and basic
attributes, onto the published root. Use a fresh path when ownership, extended metadata, or
hard-link identity matters. Archive decode accepts at most 100,000 tar entries, 128 path segments
per entry, and 200,000 distinct path nodes. Single-file and prepared-archive payloads are capped at
1.5 GB.

## What it handles

- **Reed-Solomon** error correction, including errors-and-erasures decoding driven by the colour
  classifier's own confidence
- **Cross-shard parity** or **fountain coding** so whole missing images are rebuilt without
  recapture
- **Multi-capture fusion** — several photos that each fail on their own combined into one good read
- **AES-256-GCM** encryption, binding original length, SHA-256, and filename—not the whole
  header—as associated data
- **Camera capture** — finder patterns, homography, and rectification for photos and handheld video

The wire format is fully specified in [SPEC.md](https://github.com/lfarrand/QrShard/blob/main/SPEC.md);
an independent implementation can be built from it.

## Licensing note

QrShard.Core is MIT licensed and uses **SixLabors.ImageSharp 4.0.0** under Apache-2.0 for this
open-source project; copyright (c) Six Labors. ImageSharp remains an ordinary NuGet dependency of
the Core package rather than a bundled DLL. QrShard's MIT license is included in the package.
