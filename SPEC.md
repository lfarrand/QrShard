# QrShard wire-format specification

Version: format v2 (header version 2, metadata versions 2–3), as produced by QrShard 1.1.

This document specifies the on-image format completely enough to build an independent
encoder/decoder. Everything a receiver needs is carried in the images themselves; the two
sides share no configuration. Integrity rules are part of the format: a conforming decoder
MUST verify the CRCs and the final SHA-256, so a successful decode is a cryptographic
guarantee of bit-identical data.

All multi-byte header integers are **little-endian** (C# `BinaryWriter`). Metadata-strip
fields are **MSB-first bit-packed**. "Byte k of the cell stream" means the k-th byte of the
de-imaged bitstream defined in §5.

## 1. Image geometry

Pixel constants (encode-space; the decoder measures everything relative to the frame it finds,
so captures may be cropped, padded, or uniformly rescaled):

| Constant | Value |
|---|---|
| Quiet zone (`QuietPx`) | 12 px white border |
| Locator frame (`FramePx`) | 16 px solid black ring |
| Border (`Border = QuietPx + FramePx`) | 28 px |
| Metadata strip modules | 128 |
| Resolution bounds | 700–16384 px per side |
| Cell size bounds | 1–64 px |

Outside-in structure: white quiet zone → solid black frame ring → the **inner area**
(`InnerW × InnerH`), white, containing (top to bottom):

```
gutter (white, Gutter px)
metadata strip      (MetaH px tall, 128 modules wide)
palette strip       (MetaH px tall, 2^bits blocks)
data grid           (GridW x GridH cells of CellPx px)
palette strip       (bottom copy)
metadata strip      (bottom copy)
gutter
```

`Gutter = MetaH = max(6, round(InnerW / 100))` — this shared approximation is how a decoder
finds the strip before it has read any metadata; after the CRC-validated strip is read, exact
geometry comes from its fields. Horizontal strip extent: `[gutter, InnerW - gutter)`. The data
grid starts at `x = Gutter`, `y = Gutter + 2·MetaH` within the inner area.

Both strips are duplicated top and bottom; a decoder MUST fall back between copies (metadata:
try the other copy; palette: pick or interpolate per §6).

### 1.1 Camera profile

When encoded for photo capture, the image adds top and bottom **finder bands** of height
`11·m`, where the finder module `m = clamp(round(min(W,H)/84), 8, 48)` px. Each band corner
holds a classic QR finder (7×7 modules, run signature 1:1:3:1:1) inset 2 modules from the
image corner. A solid 3×3-module **orientation tick** is centered 7 modules right of the
top-left finder's center; its mirror position near the top-right finder stays white,
disambiguating the four rotations. The frame + inner area are unchanged, shifted down by the
top band.

## 2. Metadata strip

128 one-module-wide black/white cells, dark = 1, MSB-first:

| Field | Bits | Meaning |
|---|---|---|
| magic | 8 | `0xC5` |
| version | 4 | `2` = classic interleave; `3` = same fields, v2 permuted interleave (§5.2) |
| bitsPerCell | 4 | 1–8 |
| gridW | 16 | data grid width in cells |
| gridH | 16 | data grid height in cells |
| cellPx | 8 | encoded cell size |
| metaH | 16 | strip height / gutter, px |
| innerW | 16 | inner area width, px |
| innerH | 16 | inner area height, px |
| eccParity | 8 | RS parity symbols per codeword (even, 0–64; 0 = no ECC) |
| crc16 | 16 | CRC-16/CCITT (poly 0x1021, init 0xFFFF) over the preceding 14 bytes |

Unknown versions MUST be rejected (the version nibble is the format's capability field — the
strip is packed full, so new interleaves/densities ride new version values).

## 3. Palette

`n = 2^bitsPerCell` colors. For `bitsPerCell = 1`: black then white. Otherwise bits split
per channel: `bitsR = ceil(b/3)` (i.e. `(b+2)/3` integer), `bitsG = (b+1)/3`, `bitsB = b/3`;
channel level `i` of `count` levels is `round-free (i · 255) / (count − 1)` (integer division),
or 0 when `count = 1`. Color index `v` decomposes as `iR = v / (nG·nB)`, `iG = (v / nB) mod nG`,
`iB = v mod nB`.

The palette strips draw the `n` colors as equal-width blocks in index order. Decoders classify
data cells against the **measured** strip colors (nearest squared-RGB distance), not the
theoretical palette.

## 4. Shard header

Carried at the front of every image's data stream (before ECC). Little-endian:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"QRS1"` (ASCII) |
| 4 | 1 | header version = `2` |
| 5 | 1 | flags (§4.1) |
| 6 | 8 | fileId — random per encode; groups a shard set |
| 14 | 4 | index — data: 0..count−1; parity/fountain: ordinal (§7, §8) |
| 18 | 4 | count — number of DATA images |
| 22 | 4 | payloadLength — bytes of payload in THIS image |
| 26 | 4 | payloadCrc32 — CRC-32/IEEE (poly 0xEDB88320, reflected) of the payload |
| 30 | 8 | totalLength — length of the (transformed) stream that was split |
| 38 | 8 | originalLength — length of the original file |
| 46 | 4 | stripeData — data images per stripe (0 = no cross-shard code) |
| 50 | 4 | stripeParity — parity/coded images per stripe |
| 54 | 32 | sha256 — of the ORIGINAL file (pre-compression, pre-encryption) |
| 86 | 2 | nameLen (≤ 4096) |
| 88 | nameLen | fileName, UTF-8 |
| 88+n | 4 | headerCrc32 — CRC-32/IEEE over bytes 0..88+n |

### 4.1 Flags

| Bit | Name | Meaning |
|---|---|---|
| 0x01 | Compressed | payload stream is compressed |
| 0x02 | Parity | this image is cross-shard parity / a fountain frame, not data |
| 0x04 | Brotli | compression algorithm is Brotli (else raw DEFLATE) |
| 0x08 | Encrypted | payload stream is AES-256-GCM encrypted (§9.2) |
| 0x10 | Archive | payload is a POSIX tar of a directory; extract after verification |
| 0x20 | Fountain | the parity images are random-linear fountain frames (§8) |

A decoder MUST refuse flag bits it does not know.

## 5. Cell stream, packing, and ECC

The **stream** is `header ‖ payload`. Cells are read row-major; cell `c` holds `bitsPerCell`
bits of the cell buffer MSB-first at bit offset `c · bitsPerCell`.

With `eccParity = 0`, the cell buffer IS the stream (bytes past its end render as zero cells).

With ECC: `cwCount = floor(TotalBytes / 255)` where `TotalBytes = GridW·GridH·bits/8`;
`dataLen = 255 − eccParity`. Codeword `j` carries stream slice `[j·dataLen, (j+1)·dataLen)`
(zero-padded past the stream's end). Reed-Solomon is over GF(2⁸) with primitive polynomial
**0x11D**, generator α = 2, first consecutive root α⁰ (fcr = 0), systematic encoding; the
codeword array index 0 is the HIGHEST-degree coefficient (syndromes `S_i = C(α^i)` by Horner
over the array in order).

### 5.1 Classic interleave (metadata version 2)

Cell-buffer byte `i·cwCount + j` = symbol `i` of codeword `j`, for `i ∈ [0,255)`,
`j ∈ [0,cwCount)`. Bytes `[cwCount·255, TotalBytes)` are zero padding.

### 5.2 Permuted interleave (metadata version 3)

A bijection π over `[0, cwCount·255)` is applied AROUND the classic layout: cell-buffer byte
`π(k)` = classic byte `k`. π is a Fisher-Yates shuffle of the identity array driven by a
SplitMix64 stream seeded `0x9E3779B97F4A7C15 XOR length` (length = `cwCount·255` as unsigned):

```
state = seed
next(): state += 0x9E3779B97F4A7C15
        z = state
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB
        return z ^ (z >> 31)
for i = length-1 down to 1: swap(perm[i], perm[next() mod (i+1)])
```

Padding bytes stay in place. Version 3 requires `eccParity > 0`.

## 6. Decoding requirements

- Locate the black frame ring (any position/scale in the capture); measure its inner edge.
- Read a metadata strip (either copy, small vertical search allowed); reject on CRC-16 failure.
- Measure both palette strips; classify cells against measured colors. A decoder MAY
  interpolate the reference palette per row between healthy strips (illumination gradients).
- De-interleave (§5.1/§5.2), RS-decode each codeword (erasure and trial decoding are decoder
  quality-of-implementation; the format only requires correct codewords to be accepted).
- Parse the header; verify header CRC-32, then payload CRC-32. Reject unknown flags/versions.

## 7. Cross-shard parity (Cauchy)

Data images are split into stripes of `stripeData` consecutive images; each stripe gets
`stripeParity` parity images. Payloads are zero-padded to the stripe's full per-image capacity.
Parity row `p` of a stripe with `k` data chunks: `parity_p = Σ_j M[p][j] · chunk_j` over
GF(2⁸) (0x11D), with the Cauchy matrix `M[p][j] = inverse(x_p XOR y_j)`, `x_p = p`
(p ∈ [0,stripeParity)), `y_j = stripeParity + j`. Any `k` of the stripe's `k + stripeParity`
images reconstruct it (MDS). Parity ordinal (header `index`): stripe `g = index / stripeParity`,
row `p = index mod stripeParity`. `stripeData + stripeParity ≤ 255`.

## 8. Fountain frames (flag 0x20)

Stripes of `stripeData = min(count, 64)` consecutive data images. A coded frame's payload is
`Σ_t coef_t · chunk_t` over GF(2⁸) for its stripe's chunks (padded to capacity). Ordinal
mapping: stripe `g = index mod stripes`, sequence `s = index / stripes`, where
`stripes = ceil(count / stripeData)`. The coefficient row for `(fileId, g, s)` over `k` chunks
is the first `k` bytes of the SplitMix64 stream seeded:

```
seed = fileId XOR (0x9E3779B97F4A7C15 * ((g as u32)·1000003 + (s as u32) + 1))
```

(each 64-bit output contributes its 8 bytes low-to-high). A stripe reconstructs from any set
of frames — identity rows for present data images plus coefficient rows for coded frames —
whose rows reach rank `k`. There is no bound on `s`: a sender may mint arbitrarily many
distinct frames.

## 9. Payload transforms

Applied to the whole file before splitting, in this order; reversed after reassembly:

1. **Compression** (flags 0x01/0x04): Brotli (new encoders) or raw DEFLATE (legacy), only
   kept when it shrinks the payload.
2. **Encryption** (flag 0x08): AES-256-GCM. The stream is
   `salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`; key = PBKDF2-HMAC-SHA256(password, salt,
   600 000 iterations, 32 bytes).
3. **Archive** (flag 0x10): the "file" is a POSIX tar (regular files and directories) of a
   folder; extract after SHA verification. Decoders MUST guard entry paths against escaping
   the destination.

`totalLength` is the transformed stream's length; `originalLength` and `sha256` describe the
original file (so verification happens after decrypt + decompress).

## 10. Reassembly and verification

Group shards by `fileId` (sets may be mixed in one folder; order, duplicates, and filenames
are irrelevant). Data payload lengths are the full capacity except the last image. Reassemble
via §7/§8 when images are missing, concatenate to `totalLength`, undo §9, then verify length
= `originalLength` and SHA-256. Any mismatch is a decode failure — partial or unverified
output MUST NOT be reported as success.
