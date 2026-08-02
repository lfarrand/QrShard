# QrShard wire-format specification

Version: format v2 (header version 2, metadata versions 2–4). Metadata version 4 adds error
correction to the strip (§2.2) and is what current encoders emit; versions 2 and 3 remain readable
and the golden fixtures in `tests/QrShard.Tests/golden/` pin every released minor line against the
current decoder.

**Version 4 is not readable by decoders older than it.** The version nibble is the capability
field and unknown values are rejected rather than guessed at, so a shard written by a current
encoder will not decode on an older build. That is the intended direction — old shards keep
working forever, new ones need a current reader.

Header *flags* (§4.1) provide separate feature signalling, but the one-byte field is now exhausted:
all eight bits have assigned meanings. A future capability that cannot be expressed by their valid
combinations requires a new metadata/header version or another explicitly specified extension;
current decoders must not guess at it.

This document specifies the on-image format completely enough to build an independent
encoder/decoder. Everything a receiver needs is carried in the images themselves; the two
sides share no configuration. Integrity rules are part of the format: a conforming decoder
MUST verify the CRCs and the final SHA-256, so a successful decode strongly checks that the
output matches the content declared by the shard set. This is not sender authentication: an
attacker who can replace the whole set can supply different content and its matching hash.

All multi-byte header integers are **little-endian** (C# `BinaryWriter`). Metadata-strip
fields are **MSB-first bit-packed**. "Byte k of the cell stream" means the k-th byte of the
de-imaged bitstream defined in §5.

Unless an equation explicitly says integer division, `round(x)` means IEEE 754 round-to-nearest
with ties to even (the default `Math.Round` midpoint rule in .NET). Unsigned 64-bit additions and
multiplications in the pseudorandom generators wrap modulo 2⁶⁴.

## 1. Image geometry

Pixel constants (encode-space; the decoder measures everything relative to the frame it finds,
so captures may be cropped, padded, or uniformly rescaled):

| Constant | Value |
|---|---|
| Quiet zone (`QuietPx`) | 12 px white border |
| Locator frame (`FramePx`) | 16 px solid black ring |
| Border (`Border = QuietPx + FramePx`) | 28 px |
| Metadata strip modules | 128 |
| Reference-encoder target bounds | 700–16384 px per side (grid alignment may trim the output) |
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

`Gutter = MetaH`. The reference encoder chooses
`MetaH = max(6, round((Wtarget - 2·Border) / 100))`, then trims the inner width to a whole
number of data cells. Consequently `round(InnerW / 100)` is only the receiver's initial strip
location estimate (with a small search), not a wire invariant; after the CRC-validated strip is
read, exact geometry comes from its fields. Horizontal strip extent:
`[Gutter, InnerW - Gutter)`. The data grid starts at `x = Gutter`,
`y = Gutter + 2·MetaH` within the inner area.

Both strips are duplicated top and bottom; a decoder MUST fall back between copies (metadata:
try the other copy; palette: pick or interpolate per §6).

### 1.1 Camera profile

When encoded for photo capture, the image adds top and bottom **finder bands** of height
`11·m`. The reference encoder chooses
`m = clamp(round(min(Wtarget,Htarget)/84), 8, 48)` px from the requested target dimensions;
grid alignment may trim the final image slightly. Each band corner
holds a classic QR finder (7×7 modules, run signature 1:1:3:1:1) inset 2 modules from the
image corner. A solid 3×3-module **orientation tick** is centered 7 modules right of the
top-left finder's center; its mirror position near the top-right finder stays white,
disambiguating the four rotations. The frame + inner area are unchanged, shifted down by the
top band.

## 2. Metadata strip

128 one-module-wide black/white cells, dark = 1, MSB-first.

Three versions exist. **Encoders SHOULD emit version 4**; decoders MUST read all three.

### 2.1 Versions 2 and 3 (legacy, no error correction)

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
| crc16 | 16 | CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, refin=false, refout=false, xorout=0) over the preceding 14 bytes |

Every one of the 128 modules is load-bearing: a single flipped module fails the CRC and the image
is lost, **before** the Reed-Solomon protecting the data grid is ever consulted.

### 2.2 Version 4 (error-corrected)

Same 128 modules, reallocated so the strip survives damage:

| Bytes | Contents |
|---:|---|
| 0–8 | fields, 72 bits (below) |
| 9–10 | CRC-16/CCITT-FALSE (parameters above) over bytes 0–8 |
| 11–15 | Reed-Solomon parity over bytes 0–10 |

| Field | Bits | Meaning |
|---|---|---|
| magic | 8 | `0xC5` |
| version | 4 | `4` |
| bitsPerCell | 4 | 1–8 |
| gridW | 14 | data grid width in cells (≤ 16383) |
| gridH | 14 | data grid height in cells (≤ 16383) |
| cellPx | 6 | encoded cell size **minus 1** (stores 1–64) |
| metaH | 14 | strip height / gutter, px (≤ 16383) |
| eccParity | 6 | RS parity per codeword **divided by 2** (stores even 0–64) |
| interleave2 | 1 | `1` = the v2 permuted interleave of §5.2 |
| reserved | 1 | MUST be zero |

`innerW` and `innerH` are **not carried**. A decoder derives them:

```
innerW = 2·metaH + gridW·cellPx
innerH = 6·metaH + gridH·cellPx
```

This loses nothing: versions 2 and 3 carry the values and then require them to equal exactly this,
so a strip that disagreed was never accepted.

The Reed-Solomon is the same code as §5 — GF(2⁸), polynomial 0x11D, generator α = 2, first
consecutive root α⁰ — as a shortened (16, 11) codeword. Five parity symbols correct **two symbol
errors** anywhere in the 16. Symbols are contiguous and deliberately **not** interleaved: the
damage this protects against is a burst (a mark, a cable, a scratch), and byte alignment is what
absorbs a burst, where interleaving would scatter one mark across more symbols.

A decoder MUST verify the CRC **after** correction and reject the strip if it fails. Reed-Solomon
can miscorrect beyond its bound, and a miscorrected strip would point the decoder at the wrong
geometry rather than failing.

Because the magic and version live inside the corrected region, a decoder MUST NOT reject on those
fields before running the correction — otherwise damage to them defeats the parity that exists to
repair them.

Unknown versions MUST be rejected (the version nibble is the format's capability field).

## 3. Palette

`n = 2^bitsPerCell` colors. For `bitsPerCell = 1`: black then white. Otherwise bits split
per channel: `bitsR = ceil(b/3)` (i.e. `(b+2)/3` integer), `bitsG = (b+1)/3`, `bitsB = b/3`;
channel level `i` of `count` levels is `round-free (i · 255) / (count − 1)` (integer division),
or 0 when `count = 1`. Color index `v` decomposes as `iR = v / (nG·nB)`, `iG = (v / nB) mod nG`,
`iB = v mod nB`.

The palette strips draw the `n` colors as equal-width blocks in index order. Decoders normally
classify data cells against the **measured** strip colors (nearest squared-RGB distance), so the
reference follows color transforms introduced by the display or capture. A decoder MAY fall back
to the theoretical palette when neither measured copy is usable (for example, when entries have
collapsed onto one another in both copies).

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
| 26 | 4 | payloadCrc32 — CRC-32/ISO-HDLC (reflected poly 0xEDB88320, init/xorout 0xFFFFFFFF, refin/refout=true) of the payload |
| 30 | 8 | totalLength — length of the (transformed) stream that was split |
| 38 | 8 | originalLength — length of the original source stream (file bytes, or tar bytes for Archive) |
| 46 | 4 | stripeData — data images per stripe (0 = no cross-shard code) |
| 50 | 4 | stripeParity — parity/coded images per stripe |
| 54 | 32 | sha256 — of the original source stream (pre-compression, pre-encryption) |
| 86 | 2 | nameLen (≤ 4096) |
| 88 | nameLen | fileName, well-formed UTF-8 (invalid byte sequences are rejected) |
| 88+n | 4 | headerCrc32 — CRC-32/ISO-HDLC over the half-open byte range `[0, 88+n)` |

All signed header fields use their stated little-endian two's-complement width. `index`, `count`,
`payloadLength`, `stripeData`, and `stripeParity` MUST be non-negative; `count` is 1–5,000,000,
and a data-image `index` is less than `count`. `totalLength` and `originalLength` are 0–1,500,000,000
in the reference profile, including any encryption overhead in `totalLength`. `nameLen` is at most
4096 bytes. A parity/fountain `index` may occupy the full non-negative signed-32-bit range, although
the declared ordinal space `ceil(count/stripeData)·stripeParity` is capped at 100,000,000. Values
outside these domains are malformed rather than implementation-defined.

### 4.1 Flags

| Bit | Name | Meaning |
|---|---|---|
| 0x01 | Compressed | payload stream is compressed |
| 0x02 | Parity | this image is cross-shard parity / a fountain frame, not data |
| 0x04 | Brotli | compression algorithm is Brotli (else raw DEFLATE) |
| 0x08 | Encrypted | payload stream is AES-256-GCM encrypted (§9.2) |
| 0x10 | Archive | source stream is a PAX/POSIX tar of a directory or multi-input bundle (§9.1) |
| 0x20 | Fountain | the parity images are random-linear fountain frames (§8) |
| 0x40 | AuthMeta | with 0x08: the identity fields are bound as AES-GCM associated data (§9.3) |
| 0x80 | AuthMetaV2 | with 0x08/0x40: the current AAD suite also binds transformation/archive semantics (§9.3) |

0x40 is only meaningful together with 0x08; on an unencrypted shard it has no meaning and a
decoder MUST refuse it. Encoders from v1.3.4 onward set it on every encrypted shard, so a decoder
that rejects it cannot read any current encrypted set.

0x80 is only meaningful together with both 0x08 and 0x40; a decoder MUST refuse any other
combination. Current encoders set all three bits on encrypted shards. Older authenticated sets that
have 0x40 but not 0x80 use the legacy AAD layout below.

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

### 5.1 Classic interleave (metadata version 2, or version 4 with `interleave2 = 0`)

Cell-buffer byte `i·cwCount + j` = symbol `i` of codeword `j`, for `i ∈ [0,255)`,
`j ∈ [0,cwCount)`. Bytes `[cwCount·255, TotalBytes)` are zero padding.

### 5.2 Permuted interleave (metadata version 3, or version 4 with `interleave2 = 1`)

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

Every addition and multiplication in this block wraps modulo 2⁶⁴; right shifts are logical.

Padding bytes stay in place. Metadata version 3, and version 4 with `interleave2 = 1`, require
`eccParity > 0`.

## 6. Decoding requirements

- Locate the black frame ring (any position/scale in the capture); measure its inner edge.
- Read a metadata strip (either copy, small vertical search allowed); reject on CRC-16 failure.
- Measure both palette strips; classify cells against measured colors. A decoder MAY
  interpolate the reference palette per row between healthy strips (illumination gradients), or
  regenerate the §3 theoretical palette when neither measured copy provides separable colors.
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
whose rows reach rank `k`. Unlike Cauchy parity, fountain coding has no 255-row or
originally-emitted-frame ceiling: a sender may mint additional distinct equations later. The
resulting parity ordinal must still fit the header's non-negative signed 32-bit `index` field and
the 100,000,000-element declared ordinal-space safety ceiling in §4. Arithmetic in the seed
expression wraps modulo 2⁶⁴; the `u32` casts are zero-extension of the signed non-negative inputs.

## 9. Payload transforms

### 9.1 Archive packaging (flag 0x10)

Archive packaging happens **before** compression and encryption. The source stream is a PAX tar
when the user selects one folder or more than one input. A single folder's contents occupy the tar
root; a multi-input bundle keeps each selected input's name as its first path component.

The portable archive profile contains only regular files and directories. Empty directories are
emitted. The current QrShard CLI archive builder rejects a symbolic link/junction selected as a
top-level input and skips reparse-point entries found while walking a folder, rather than following
them. Multiple paths to one hard-linked file are emitted as independent regular-file entries. The
archive carries Unix regular-file owner/group/other rwx bits (including executability); extraction
applies them subject to the receiver's umask, while .NET strips setuid, setgid, and sticky special
bits. Ownership, ACLs, extended attributes, alternate data streams, sparse-file state, directory
modes/metadata, and hard-link identity are not part of the format's portable guarantee.

Entry names MUST be safe relative paths: no absolute paths, `.`/`..` segments, backslash path
separators, unsafe/non-portable platform names, or path components that collide after Unicode NFC
normalization followed by invariant uppercase mapping (`ToUpperInvariant` in .NET). A decoder MUST
reject links, devices, and any other non-regular/non-directory entry type, and MUST guard the
resolved target against escaping the destination. An implementation whose globalization runtime
cannot perform full Unicode normalization/casing MUST reject non-ASCII archive names rather than
silently using reduced invariant-globalization tables.

For an Archive payload, `originalLength` and `sha256` describe the exact **tar byte stream**, not
the sum/hash of extracted files. A decoder verifies those tar bytes before extraction.

### 9.2 Compression and encryption

After optional archive packaging, transforms are applied to the whole source stream in this order:

1. **Compression** (flags 0x01/0x04): Brotli (current encoders) or raw DEFLATE (legacy), only
   kept when it shrinks the stream.
2. **Encryption** (flag 0x08): AES-256-GCM. The encrypted stream is
   `salt(16) ‖ nonce(12) ‖ tag(16) ‖ ciphertext`; key = PBKDF2-HMAC-SHA256(password, salt,
   600 000 iterations, 32 bytes). Salt and nonce are freshly random per encode. The password is
    encoded as UTF-8 exactly as supplied, with **no Unicode normalization**. Flags 0x40/0x80 select
    the GCM associated-data suite in §9.3; without 0x40, the associated data is empty.

`totalLength` is the final transformed stream's length. For a non-archive it is reversed as
decrypt → decompress → length/SHA-256 verification. For an archive it is reversed as decrypt →
decompress → tar-byte length/SHA-256 verification → extraction. Thus `originalLength` and
`sha256` always describe the stream immediately before compression/encryption: ordinary file bytes
or, when 0x10 is set, the packaged tar bytes.

### 9.3 Authenticated metadata (flags 0x40 and 0x80)

The header is protected by a CRC-32, which is an error check and not a MAC: anyone who alters a
header can recompute it. The original length, SHA-256, and file name are therefore bound into the
encryption itself, so rewriting any of those identity fields fails authentication rather than
decoding to the right bytes under the wrong name or length. This does not authenticate the whole
header; the exact exclusions and their consequences are stated below.

For legacy authenticated shards with 0x08/0x40 set and 0x80 clear, the AES-GCM **associated data**
is the concatenation

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | `originalLength`, little-endian signed 64-bit |
| 8 | 32 | `sha256` of the original source stream (file or archive tar) |
| 40 | *n* | `fileName`, UTF-8, exactly the header's bytes, no terminator |

giving `40 + n` bytes total.

For current shards with 0x08/0x40/0x80 set, the AAD is:

| Offset | Size | Field |
|---|---|---|
| 0 | 48 | UTF-8/ASCII domain `QrShard-AAD-v2:AES-256-GCM:PBKDF2-SHA256-600000` followed by NUL |
| 48 | 1 | the complete flags byte with only the per-image Parity bit (0x02) cleared |
| 49 | 8 | `originalLength`, little-endian signed 64-bit |
| 57 | 32 | `sha256` of the original source stream |
| 89 | 4 | UTF-8 file-name byte length, little-endian signed 32-bit |
| 93 | *n* | `fileName`, UTF-8, exactly the header's bytes, no terminator |

This domain fixes the cipher/KDF interpretation and binds compression, archive, fountain, and AAD
suite flags. Rewriting and re-checksumming Archive can therefore no longer turn one authenticated
ordinary file into an extraction operation (or vice versa). `fileId`, ordinal/count, recovery
geometry, `totalLength`, and per-image payload length/CRC remain outside the AAD; family,
reassembly-length, payload CRC, and final SHA checks validate them before publication.

For an empty non-archive file the encoder encrypts an empty plaintext with `originalLength = 0` and
the SHA-256 of zero bytes, so the construction is unchanged.

**Backward compatibility.** Shards written before v1.3.4 set 0x08 without 0x40 and use empty AAD.
Shards with 0x40 but not 0x80 use the legacy 40+*n* layout; shards with both use the current layout.
A decoder MUST decide from the flags, not from the presence of a password. GCM treats empty and
absent associated data identically, so an implementation may pass a zero-length buffer for the
oldest suite.

A decoder MUST NOT fall back to an older/empty associated-data suite when authenticated decryption
fails — a tampered header is exactly what that failure means.

## 10. Reassembly and verification

Group shards by `fileId` (sets may be mixed in one folder; order, duplicates, and filenames
are irrelevant). Data payload lengths are the full capacity except the last image. Reassemble
via §7/§8 when images are missing, concatenate to `totalLength`, undo §9, then verify length
= `originalLength` and SHA-256. Any mismatch is a decode failure — partial or unverified
output MUST NOT be reported as success.

For a data set with per-image payload capacity `cap`, `totalLength` MUST satisfy
`(count−1)·cap < totalLength ≤ count·cap`, except that a one-image untransformed empty stream may
have `totalLength = 0`. Every data image before the last has payload length `cap`; the last has
`totalLength − (count−1)·cap`. This floor is checked before allocating or concatenating chunks.

The QrShard reference decoder writes a single-file result to an unpredictable same-filesystem
staging path and atomically publishes it only after successful verification. Archive extraction is
staged in a private sibling directory and published only after every validated entry succeeds; the
final archive destination MUST be absent or empty and is never merged with an existing tree. New
reference-decoder destinations retain private staging permissions (requested 0600 files / 0700
archive roots on Unix, or stricter after umask, and protected owner-only DACLs on Windows). An
existing explicit single-file destination retains only its nine Unix rwx bits, or its Windows DACL
and basic attributes. An existing empty archive destination instead retains its full Unix directory
mode (including setgid/sticky policy bits), or its Windows DACL and basic attributes; that is root
policy supplied by the receiver, not directory mode from the archive. Single-file publication is
an atomic same-filesystem move. Archive publication exposes only a complete tree, but replacing an
existing empty destination directory is not specified as one atomic filesystem operation.
