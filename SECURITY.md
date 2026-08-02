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

What it does **not**: the tool writes with the permissions of whoever runs it, so an explicit
single-file `-o` can replace a file that user could already replace. Point `-o` somewhere
sensible. Archive output is stricter: its destination must be absent or empty and is never merged
into an existing tree.

## What the format guarantees

A successful decode means the reconstructed file matched a SHA-256 carried inside the shards, so
bit-exactness is verified rather than assumed. Beneath that sit a CRC-32 per payload and
CRC-32-protected headers.

Header integrity is a **checksum, not a MAC**: CRC-32 detects accidental corruption, and an
attacker who rewrites a header can recompute it. Current encrypted shards use the AuthMetaV2
AES-GCM associated-data suite: it domain-separates the cipher/KDF parameters and binds the original
size, SHA-256, exact UTF-8 file name, and family-wide transformation flags. In particular, Archive
cannot be toggled to turn an authenticated ordinary payload into an extraction operation.
Per-image ordinal/payload fields and recovery geometry remain CRC/family/final-SHA checked rather
than MACed. Legacy encrypted shards retain their documented older AAD behavior.
**Treat every shard set as attacker-modifiable in transit at the unbound-header level**; for an
unencrypted set, no header field has cryptographic authentication.

Encryption is AES-256-GCM with a PBKDF2-SHA256 key at 600 000 iterations, a per-file random salt
and nonce.

### Password handling

`-p/--password` can be retained in shell history or visible through process inspection. Prefer
`--password-file` (strict UTF-8, optional UTF-8 BOM, 64 KiB/4096-character cap) with suitable local
permissions, or `--password-stdin` (one bounded line). The three sources are mutually exclusive;
there is deliberately no environment-variable source. Wrong-password or authentication failure does not
publish plaintext, but encryption does not hide the shard geometry, file name, original length,
image count, or other cleartext header metadata.

The password is converted to UTF-8 exactly as supplied before PBKDF2; QrShard does not apply
Unicode normalization. Visually identical composed and decomposed strings are therefore different
passwords.

Session files contain the captured shard payloads themselves. For an unencrypted transfer that is
plaintext, not merely metadata. A v2 session is atomically initialized with private permissions,
then updated under an exclusive persistent `.lock` sidecar as an append-only CRC-framed journal.
The final incomplete record can be recovered; interior corruption fails closed. Same-family valid
candidates that disagree for one ordinal become a compact durable erasure, so neither arrival order
selects a winner; inconsistent family metadata is rejected instead of persisted. Cleanup quarantines
and authenticates the exact opened object before deletion. Protect the
session and its reserved `.lock` sidecar like the source; session-backed explicit output must be a
fresh path.

### Verified output and archive boundaries

The current decoder does not stream unverified bytes into the final pathname. A single-file
restore is written to an unpredictable same-directory staging file, checked for exact length and
SHA-256, then atomically moved into place. Verification, decompression, and pre-publication I/O
failures leave the previous destination intact, and the staging file is removed best-effort. Once
the move commits, the old filesystem object is gone; a later Windows attribute-restoration failure
can therefore report an error with the verified replacement already present.

"Atomic" here describes what concurrent filesystem users see; it is not a power-loss durability
guarantee. QrShard does not fsync every restored file and parent directory. Keep the source shards
or another backup until the receiving storage has been persisted and independently checked when
crash recovery matters.

Atomic replacement creates a new filesystem object. If explicit `-o` replaces an existing file,
QrShard copies its Unix rwx mode bits, or its Windows DACL and basic file attributes. It does not
preserve ownership, timestamps, ACL details outside that Windows DACL, extended attributes,
alternate data streams, sparse-file state, or hard-link identity. Choose a fresh destination path
when preserving those properties matters. A new single-file output retains the staging object's
private security (requested Unix 0600, or stricter after umask, or a protected owner-only Windows
DACL); a new archive root likewise requests Unix 0700 or uses an owner-only DACL.

The output parent is itself a trust boundary. Use a directory that untrusted local users cannot
rename or delete entries in; private staging permissions do not make path-based publication safe
against a principal that already has those rights on the parent directory.

For an archive, the decrypted/decompressed tar is first length- and SHA-256-verified in a private
temporary directory. Entries are then validated and extracted into a private sibling directory;
the complete tree is published only after every entry succeeds. A destination that is a file or a
non-empty directory is refused. Entry paths must be safe relative paths and must not collide by
case or Unicode normalization. Only regular files and directories are accepted: symbolic links,
hard-link entries, devices, and other special tar types are rejected. The bundled encoder skips
reparse-point links inside selected folders (and rejects one selected as a top-level input), while
hard-linked paths are copied as independent regular files.

If the explicit archive destination is an existing empty directory, the published root carries
forward its full Unix directory mode, including setgid/sticky policy bits, or its Windows DACL and
basic attributes. Ownership, timestamps, extended attributes, and other metadata are not carried.
This destination-root policy is separate from archive entry metadata. Archive publication is
complete and non-merging; unlike the single-file move, replacing that empty directory is not
promised as one atomic filesystem operation.

Archive transfer is not a backup format. It carries Unix regular-file owner/group/other rwx bits,
including executability; extraction applies them subject to the receiver's umask, while .NET strips
setuid, setgid, and sticky special bits. Ownership, ACLs, extended attributes, alternate data
streams, sparse-file state, directory modes/metadata, and hard-link identity are outside the
portable contract.

### Resource limits

Single-file and prepared-archive payloads are capped at 1.5 GB. Ordinary image loads have a
500-million-pixel ceiling and a separate pre-load admission charge of roughly 6 bytes/pixel against
`DecodeMemoryBudgetMB`. Batch worker planning uses the largest identifiable input at roughly
40 bytes/pixel; with the 4000 MB default this is about 332 MB/worker and 12 workers for 4K, or
1.92 GB/worker and 2 workers for 48 MP. The same setting also bounds actual path/input
materialization, retained CRC-valid payloads/count, fusion salvage/work, lifetime watch/video
state, retained CLI journal state, and the public incremental session's default retained state.
Embeddings can give `QrShardDecodeSession` a smaller explicit decimal-MB ceiling; rejected additions
do not mutate it. These are independent conservative admission ceilings, not one subtractive pool
or a hard process-working-set limit. Photo fusion admits at most eight compatible captures per layout,
1024 layouts and 512 MiB of work, with retained cells limited to one eighth of the decode budget.
Sessions admit at most one million unique keys/two million frames, and the physical journal is
capped at three times the decoded-byte budget. APNG slideshow creation and native animated-image recording decode are capped at 256 MiB
of decoded RGB frames; animated recording input is additionally capped at 4096 frames. Archive
encode/decode is capped at 100,000 entries and 128 path segments per entry; decode also caps its
collision-checking trie at 200,000 distinct path nodes.

External helpers are never shell-invoked. A configured helper must be absolute; otherwise QrShard
uses an absolute PATH lookup that skips relative, current, and application directories (and
requires an executable file on Unix). ffmpeg receives structured arguments, a restricted child
PATH, `-nostdin`, one worker/filter thread, protocol/pixel limits, bounded stderr and termination
waits. `xrandr` and browser launchers use the same trusted-resolution policy and bounded execution
where output is consumed. Terminal diagnostics replace control, bidi-format, line/paragraph
separator, and invalid-surrogate characters and cap hostile names/messages; JSON uses JSON escaping.

### Static-analysis trust boundary

CodeQL scans production C# and GitHub Actions on pull requests, `main`, a weekly schedule, and manual
dispatch. C# analysis keeps CodeQL's default remote model and adds the `file` threat submodel because
bytes read from shard, image, archive, and session files are untrusted. It deliberately does not
enable the entire preview `local` group: that group also treats command-line arguments, environment
variables, and `Path.GetTempPath()` as attacker-controlled, although they are explicit same-user
configuration here. On this file-processing CLI that broader model reports every intended input,
output, temporary path, and absolute helper launch as path or command injection. Those boundaries
are instead enforced by containment/collision checks, private staging and reparse-point handling,
absolute helper resolution, structured argument lists, and their adversarial regression tests.
Test-source files are excluded from the production CodeQL database; they still run under the full
cross-platform test matrix. Revisit this model before adding a service, remotely supplied path, or
different process-launch boundary.

### Release artifacts

Beginning with v1.6.2, tagged releases publish `SHA256SUMS` only after the exact Native-AOT binaries
and NuGet packages have been tested with .NET SDK 10.0.302 on versioned GitHub-hosted runner labels.
An active no-bypass tag ruleset blocks update/deletion of `v*`, and the workflow independently
peels the remote tag and compares it with the event commit before attestation, draft creation, and
publication. GitHub
stores signed SLSA build-provenance for every release file, including `SHA256SUMS`, and signed
SPDX 2.2 SBOM attestations generated separately for each binary archive and package. Native-AOT
archives use their RID-specific restore graph, including the corresponding runtime/compiler packs;
tool/core packages use their ordinary framework-dependent graphs. Verify them using the constrained
commands in README. Releases produced by this workflow are immutable, so GitHub locks their assets
and tag and creates an additional release attestation. Older releases predate these controls.

Write-enabled promotion jobs never check out or build repository code. Package publication first
validates the exact Tool/Core candidates, bounded ZIP structure, and every existing version across
NuGet.org and GitHub Packages. NuGet.org copies must be repository-signed and semantically match
apart from that signature; GitHub copies must match byte-for-byte. Only after all present copies
pass are missing immutable versions pushed and downloaded again, making an ambiguous partial
publication safely rerunnable. Draft assets use the same fail-closed exact-inventory policy.

These attestations authenticate artifact digests and workflow provenance, not platform publisher
identity. The Windows executable is not Authenticode-signed. The workflow does not inspect the
macOS signature; Native AOT is expected to produce only an ad-hoc signature, not a Developer ID
signature or notarization. `SHA256SUMS` is plaintext and must not be trusted without its attestation.
The release executable is `QrShard.exe` on Windows and case-sensitive `QrShard` on Unix.
The `linux-x64` and `linux-arm64` Native-AOT assets require glibc 2.35 and 2.39 respectively.

## Known advisories

### Availability and diagnostic-output failures (fixed through 1.6.1)

These did not produce wrongly verified bytes, but they could terminate a batch on one malformed
image, suppress restoration of complete siblings, size diagnostic buffers from hostile metadata,
or make terminal output misleading. The table separates their affected ranges and fix versions.

| Defect | Affects | Fixed in |
|---|---|---|
| Terminal escape sequences in a header file name reaching the console unescaped | v1.0.0 – v1.4.0 | **1.5.0** |
| `Image.Identify` in the worker-size probe raising an unlisted exception type | v1.5.2 – v1.5.2 | **1.6.0** |
| The same, in the two single-image load paths | v1.0.0 – v1.6.0 | **1.6.1** |
| The same, in `VideoDecoder.IsAnimatedImage` — reached by `decode` before any decoder runs | v1.0.0 – v1.6.0 | **1.6.1** |
| The same, in `RecordingFrameSource`'s two frame loaders (no filter at all) | v1.0.0 – v1.6.0 | **1.6.1** |
| An incomplete file discarding every complete file grouped after it | v1.0.0 – v1.6.0 | **1.6.1** |
| Diagnostics and heatmap buffers sized from an unvalidated metadata strip | v1.3.4 – v1.6.0 | **1.6.1** |

The four `Image.Identify` rows share one root cause across six call sites: exception allowlists
were used where the actual policy was that one malformed image must not abort a batch. The 1.6.0
fix broadened one filter but left neighbouring call sites enumerating exception types. The
terminal-output, group-ordering, and diagnostic-buffer rows are separate defects. The durable
image-load mitigation is the blanket per-image `catch` in `CollectShards`: a policy rather than a
growing list of decoder exceptions.

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

In the interactive CLI, reaching these historical defects required the operator to decode an
attacker-controlled shard image. An embedding service that automatically accepts uploaded images
should instead treat that input as remotely supplied. **Take the latest stable release** if you
decode images from anywhere you do not control, and pass `-o` explicitly regardless.

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
recommendation — fixes go to the latest release only, so take the latest stable release:

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
