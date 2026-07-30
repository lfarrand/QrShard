# Security

## Reporting a vulnerability

Please report privately through GitHub's
[**Report a vulnerability**](https://github.com/lfarrand/QrShard/security/advisories/new) form
rather than opening a public issue. That opens a private thread with the maintainer and, if it is
a real issue, becomes the advisory that notifies people who depend on the packages.

Useful things to include: the version, the command or API call, and a crafted shard image or
payload that reproduces it. There is no bounty.

## Threat model

**Decoding consumes untrusted input.** That is the point of the tool: images arrive from another
machine, over a channel nobody controls, and may have been produced by someone else entirely.
Every field in a shard header, every byte of a payload, and every pixel of a captured image is
therefore attacker-controlled data, and the decoder is written on that basis.

What the project treats as a vulnerability:

- a crafted shard image causing a write outside the output path the user chose
- a crafted shard image causing code execution, or unbounded memory/CPU consumption
- a decode reporting success while producing bytes that are not the ones encoded
- an encrypted payload being recoverable without the password, or a tampered one decrypting
  without an error

What it does **not**: the tool writes with the permissions of whoever runs it, so a decode can
overwrite files that user could already overwrite. Point `-o` somewhere sensible.

## What the format guarantees

A successful decode means the reconstructed file matched a SHA-256 carried inside the shards, so
bit-exactness is verified rather than assumed. Beneath that sit a CRC-32 per payload and
CRC-32-protected headers.

Header integrity is a **checksum, not a MAC**: CRC-32 detects accidental corruption, and an
attacker who rewrites a header can recompute it. For encrypted payloads this is closed by binding
the cleartext identity fields (original size, SHA-256, file name) as AES-GCM associated data, so a
tampered header fails decryption up front. For unencrypted payloads it is not closed, and cannot
be — an unauthenticated format has no secret to authenticate with. **Treat an unencrypted shard
set as attacker-modifiable in transit**; if that matters, use `-p`.

Encryption is AES-256-GCM with a PBKDF2-SHA256 key at 600 000 iterations, a per-file random salt
and nonce.

## Known advisories

### Integrity: a crafted shard could make a decode report success wrongly (fixed in 1.5.0)

Three separate defects, all reachable from an ordinary `qrshard decode` of images the user chose
to accept, and all landing on the outcome this project treats as the serious one — **a decode
reporting success while producing bytes that are not the ones encoded**, or none at all.

| Defect | Affects | Fixed in |
|---|---|---|
| Header file name naming a Win32 device (`NUL`, `CON`, `COM1`…) | v1.0.0 – v1.4.0 | **1.5.0** |
| Reed-Solomon erasure decode spending its whole verification margin | v1.0.0 – v1.4.0 | **1.5.0** |
| Metadata strip declaring zero complete codewords with ECC on | v1.0.0 – v1.4.0 | **1.5.0** |

**The device name is the one to understand.** On Windows, opening `<dir>\NUL` succeeds, discards
every byte, and creates no file — and `File.Exists` on it is false, so the collision check never
diverted. The SHA-256 could not catch it either, because it is computed over the payload as it is
written rather than read back: `written` still matched, the digest still matched, and the decode
printed `SHA-256 verified` over a file that does not exist. Total data loss presented as success.
Reachable only without `-o`, since an explicit output path is used exactly as given.

The Reed-Solomon defect is a silent miscorrection. Two syndromes are meant to stay unspent so the
final check can detect a wrong answer, but the reserve was enforced against the erasure count
only — one extra error at the maximum erasure count consumed it, leaving the verification
vacuous. Erasure flags come from the colour classifier's confidence, so "more errors than were
flagged" is an ordinary condition rather than an exotic one.

The third let the FEC pass write nothing and report success, so the per-worker recovered buffer
was handed on still holding the *previous* image's stream — valid header CRC, valid payload CRC —
and a shard was accepted from an image that contributed none of its bytes.

Alongside these, 1.5.0 bounds a set of denial-of-service paths where a small crafted image could
size gigabytes of buffers or stall a decode, and stops one malformed image aborting a whole folder
decode and discarding every other image's successful result. Those are availability rather than
integrity, and are not itemised here.

None of these is remotely exploitable: every one requires a shard image the user chooses to
decode. **Take the current release (1.6.0)** if you decode images from anywhere you do not
control, and pass `-o` explicitly regardless.

### Path traversal via the shard header's file name (fixed in 1.3.10)

A crafted image could steer the decode write
outside the output directory, or to an absolute path, truncating the target before verification
ran. Reachable from `qrshard decode <folder>` with no `-o`, and from `DecodeImages` with no
`outputPath`.

It was fixed in two parts, because the first fix missed one of them.

| Payload | Affects | Fixed in |
|---|---|---|
| Single file | v1.0.0 – v1.3.8 (packages 1.3.5 – 1.3.8) | 1.3.9 |
| Archive — a folder, carried as a tar | v1.0.0 – v1.3.9 (packages 1.3.5 – 1.3.9) | **1.3.10** |

1.3.9 sanitized the single-file destination only. The archive branch still derived its directory
from the header via `Path.GetFileNameWithoutExtension`, which is not a sanitizer — a name of
`...` becomes `..`, the parent directory — and the tar extractor's own containment check was then
anchored to that already-escaped root.

**1.3.10 is the first release in which both are fixed**, and it is the floor rather than the
recommendation — fixes go to the latest release only, so take the current one (1.6.0):

```
dotnet tool update -g QrShard.Tool
dotnet add package QrShard.Core
```

or the latest binaries. If you cannot upgrade, always pass an explicit output path — `-o` on the
command line, or `outputPath` to `DecodeImages` — which is used exactly as given and is not
influenced by the header.

Packages 1.3.5 – 1.3.9 are unlisted and deprecated on nuget.org. They still resolve for anyone
who references them explicitly, so existing builds do not break.

## Supported versions

Fixes go to the latest release only. There are no maintained release branches.
