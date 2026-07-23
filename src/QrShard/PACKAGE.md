# QrShard

Transfer files between machines **through the screen**. QrShard encodes any file or folder into a
series of dense, QR-style images, which you display on one machine and capture on another — by
screenshot, phone photo, screen recording, or live webcam — and reconstitutes the original file
**bit-for-bit, verified by SHA-256**.

Useful when there is no network path: locked-down VDI/RDP sessions, air-gapped machines, kiosks, or
anywhere the clipboard and file sharing are disabled but the screen is visible.

## Install

```
dotnet tool install -g QrShard.Tool
```

That provides the `qrshard` command. Tagged releases also attach Native-AOT single-file binaries
for win-x64 / linux-x64 / linux-arm64 / osx-arm64, which need no .NET runtime.

## Use

```
qrshard encode holiday-photos.zip          # a folder works too, tar-ed automatically
qrshard decode captures/ -o holiday-photos.zip
```

Capture each displayed image at 100% zoom, put the captures in a folder in any order, and decode.
Damaged captures are repaired by error correction, fused from multiple failed photos, or rebuilt
from parity images; anything unrecoverable is reported by exact part number.

Other modes:

```
qrshard send report.pdf --video            # slideshow you record instead of capturing by hand
qrshard decode recording.mp4 -o report.pdf # decode straight from a screen recording
qrshard receive --device "Integrated Camera"   # live decode from a webcam
qrshard receive --screen                   # decode this machine's own screen, e.g. an RDP window
qrshard calibrate                          # find the densest settings your capture chain survives
```

Density ranges from ~212 KB per image at the robust default to ~4.9 MB filling a 4K display, so a
100 MB file fits in 22 screenshots. Add `-R 10` for parity images so lost captures need no redo,
`-p <password>` for AES-256-GCM encryption, or `--camera` to make shards decode from photos.

Full documentation: <https://github.com/lfarrand/QrShard>

## Licensing note

QrShard is MIT licensed and bundles **SixLabors.ImageSharp 4.x**, which ships under the
[Six Labors Split License](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE) — free for
open-source and personal use, with commercial use requiring a paid licence from Six Labors. See
their [pricing](https://sixlabors.com/pricing/) if you intend to use this tool commercially.
