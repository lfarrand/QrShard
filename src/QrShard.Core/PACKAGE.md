# QrShard.Core

The embeddable codec behind [QrShard](https://github.com/lfarrand/QrShard): encode any file into
dense, QR-style images and decode captures of them — screenshots, phone photos, screen recordings,
webcam frames — back into the original file, **bit-for-bit, verified by SHA-256**.

The image format is custom (not QR-standard) and tuned for screen-to-screenshot transfer. Because
a screenshot is a lossless pixel copy, each image carries far more than a real QR code: from
~212 KB per image at the robust default up to ~4.9 MB filling a 4K display.

## Install

```
dotnet add package QrShard.Core
```

Each release is also mirrored to GitHub Packages, but **nuget.org is the supported install
source** — GitHub Packages requires an access token with `read:packages` to install from, even
for public repositories.

## Use

```csharp
using QrShard.Core;

// Encode a file into shard images.
var result = QrShardCodec.EncodeFile("holiday-photos.zip", "out-dir");

// Decode captures back into the original file.
QrShardCodec.DecodeImages(Directory.GetFiles("captures"), "holiday-photos.zip");
```

For capture that arrives over time, `QrShardDecodeSession` decodes incrementally: feed images
(paths or in-memory bytes) as they land, ask which are still missing, and assemble the moment the
set becomes recoverable.

Shards are order-independent, duplicate-tolerant and filename-agnostic, and shards belonging to
different files can share a folder without being confused for one another.

## What it handles

- **Reed-Solomon** error correction, including errors-and-erasures decoding driven by the colour
  classifier's own confidence
- **Cross-shard parity** or **fountain coding** so whole missing images are rebuilt without
  recapture
- **Multi-capture fusion** — several photos that each fail on their own combined into one good read
- **AES-256-GCM** encryption, binding the cleartext identity fields as associated data
- **Camera capture** — finder patterns, homography, and rectification for photos and handheld video

The wire format is fully specified in [SPEC.md](https://github.com/lfarrand/QrShard/blob/main/SPEC.md);
an independent implementation can be built from it.

## Licensing note

QrShard.Core is MIT licensed, but it depends on **SixLabors.ImageSharp 4.x**, which ships under the
[Six Labors Split License](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE) — free for
open-source and personal use, with commercial use requiring a paid licence from Six Labors. Review
their [pricing](https://sixlabors.com/pricing/) before using this package in a commercial product.

ImageSharp's build-time licence check ships in its `build/` folder rather than `buildTransitive/`,
so it does not run in projects that reference QrShard.Core. That is a packaging detail, not a grant
of rights — the Split License still governs your use.
