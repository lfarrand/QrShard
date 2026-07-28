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

| Advisory | Affects | Fixed in |
|---|---|---|
| Path traversal via the shard header's file name — a crafted image could steer the decode write outside the output directory, or to an absolute path, truncating the target before verification ran | v1.0.0 – v1.3.8 (NuGet packages 1.3.5 – 1.3.8) | **1.3.9** |

Upgrade with `dotnet tool update -g QrShard.Tool`, `dotnet add package QrShard.Core --version
1.3.9`, or by taking the v1.3.9 binaries. If you cannot upgrade, always pass an explicit output
path — `-o` on the command line, or `outputPath` to `DecodeImages` — which is used exactly as
given and is not influenced by the header.

## Supported versions

Fixes go to the latest release only. There are no maintained release branches.
