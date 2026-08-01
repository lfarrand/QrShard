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
for win-x64 / linux-x64 / linux-arm64 / osx-arm64, which need no .NET runtime. The tool package
itself targets and requires **.NET 10**.

Release executables are named `QrShard.exe` on Windows and case-sensitive `QrShard` on Unix. The
Linux Native-AOT floors are glibc 2.35 (`linux-x64`) and glibc 2.39 (`linux-arm64`). Binary archives
are built with .NET SDK 10.0.302. Beginning with v1.6.2, GitHub stores signed SLSA provenance and an
artifact-specific SPDX 2.2 SBOM attestation for their exact release bytes. Each binary SBOM uses
its RID-specific Native-AOT restore graph; the tool-package SBOM uses the ordinary package graph.
Releases produced by the current workflow are immutable. Verification instructions are in the
repository README; older releases predate these controls. These controls are not platform
signatures: Windows is not
Authenticode-signed, and macOS output is expected to be ad-hoc only rather than Developer ID signed
or notarized. `SHA256SUMS` is plaintext but is itself covered by provenance.

Each release is also mirrored to GitHub Packages, but **nuget.org is the supported install
source** — GitHub Packages requires an access token with `read:packages` to install from, even
for public repositories.

NuGet.org repository-signs the package after upload, changing its bytes from the pre-publication
`.nupkg` attached to GitHub Releases. NuGet clients can verify that repository signature separately,
subject to platform and client policy; the GitHub attestation applies to the GitHub Release copy.

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
qrshard decode captures/ --json            # machine-readable result, for scripting
```

The HTML slideshow is a relative manifest: keep `slideshow.html` beside its shard images and any
generated PNG sidecars. `--slideshow apng` makes a single file but refuses a set above 256 MiB of
decoded RGB frames; use HTML for larger transfers.

Density ranges from ~212 KB per image at the robust default to ~4.9 MB with the Max4K profile and
~6.5 MB at 8-bit density on a 4K display, so a 100 MB file fits in 22 Max4K screenshots. Add
`-R 10` for per-stripe parity, `-p <password>` for AES-256-GCM encryption, or `--camera` to make
shards decode from photos. Passwords passed with `-p` may be visible in shell history and process
listings.

Decoded single files are staged, length/SHA-256-verified, and atomically moved into place. Archive
destinations must be absent or empty; extraction never merges into an existing tree, and the
complete staged tree is published only after every entry succeeds. Folder archives preserve
ordinary/empty directories and carry Unix regular-file owner/group/other rwx bits (including
executable); extraction applies them subject to the receiver's umask and strips
setuid/setgid/sticky bits. Folder archives also skip reparse-point links, copy hard-linked names as
independent files, reject non-portable path aliases, and do not promise ownership, ACL, xattr,
alternate-stream, sparse-file, directory-mode/metadata, or hard-link fidelity.
Replacing an existing single-file `-o` carries forward only its nine Unix rwx bits, or its Windows
DACL and basic attributes. An existing empty archive destination instead carries its full Unix
directory mode (including setgid/sticky policy bits), or its Windows DACL and basic attributes,
onto the published root. Use a fresh path if ownership, extended metadata, or hard-link identity
matters. Archives are limited to 100,000 entries and 128 path segments per entry; decode also caps
the portable path index at 200,000 distinct nodes. Single-file and prepared-archive payloads are
capped at 1.5 GB.

Full documentation: <https://github.com/lfarrand/QrShard>

## Licensing note

QrShard is MIT licensed and uses **SixLabors.ImageSharp 4.0.0** under Apache-2.0 for this
open-source project; copyright (c) Six Labors. The tool package carries the Apache-2.0 text,
QrShard's MIT license, and the exact reviewed .NET third-party notices beside the bundled DLLs.
