## Learned User Preferences

- Merge pull requests into `main` with `--merge`, not squash.
- Do not commit `.codex/config.toml` (machine-local Rider MCP), `.serena/`, or `.cursor/hooks/state/`.
- Do not retag `v*` releases; the Protect release tags ruleset makes those tags immutable.
- Filter tests with xunit.v3 MTP `--filter-class`, not VSTest `--filter`. Stacking multiple `--filter-class` flags ANDs them (zero tests / exit 8); use one flag or a full run. Fuzz CI uses `dotnet test ... -- --filter-class QrShard.Tests.FuzzTests`.
- RemoteDisplayCapture (MIT, same author; owner is `lfarrand`, not `lfardand`) is absorbed from commit `8e8594f7`. Do not invent RDC APIs. Do not add `display`/`record` verbs to `Cli.ArgSpecs`. Do not rewrite `PACKAGE.md` as if the NuGet tool ships the WPF/DXGI hosts. Do not put `UseWPF` / `net10.0-windows` on `QrShard.slnx` (Linux CI would fail).

## Learned Workspace Facts

- This repository has no SQL.
- SDK is 10.0.400 with `rollForward: disable`. Native AOT runtime pack is 10.0.11; Hashing/DI stay 10.0.10.
- The `publish-nuget` job has no checkout; pin `dotnet-version` 10.0.400, not `global.json`.
- Header flags are exhausted (`KnownFlags` 0xFF); the next capability needs a version bump.
- `QrShardDecodeSession` retains failed ECC captures and runs `PhotoFusion.Fuse` from 1.7.6. nuget.org / tagged Native-AOT **1.7.5** skipped that session path; folder `DecodeImages` already fused on 1.7.5.
- Session v1-migrate TOCTOU tests must not poll for staging files (the race never plants on a fast machine). Use the `SessionStore.TestingBeforeReplaceExistingPublish` hook.
- Windows sidecars `QrShard.Display` and `QrShard.Recorder` live in `QrShard.Windows.slnx` (`net10.0-windows`). `Vortice.Direct3D11` is pinned at 3.8.3 on `QrShard.Recorder` only. Testable internals are in `src/QrShard.Windows.Logic` (`net10.0`) on `QrShard.slnx` so Linux CI can test them.
- From 1.7.6, tagged GitHub Releases attach self-contained win-x64 zips `qrshard-display-win-x64.zip` and `qrshard-recorder-win-x64.zip` plus matching SPDX SBOMs. They are not in nuget.org, not in GitHub Packages nupkgs, and not inside Native-AOT CLI archives. `publish-nuget` stays exactly `QrShard.Tool` + `QrShard.Core`.
- `release.yml` `create-draft`/`publish-release` `expected[]` is exact-count; adding host zips requires extending both lists (16 files + `SHA256SUMS`). The `windows-hosts` job on `windows-2025` publishes them; do not launch `Display.exe` in CI (WPF MessageBox hangs). Recorder no-args usage smoke only.
- `QrShard.Display` / `QrShard.Recorder` / `QrShard.Windows.Logic` `Version` must match the tag (preflight). Do not put `Version` in `Directory.Build.props`.
- HTML `SlideshowWriter` and ffmpeg `receive --screen` (gdigrab, not DXGI) remain the cross-platform path. Do not fold DXGI/WPF into Core.
- User-facing docs: session PhotoFusion is shipped in 1.7.6; describe the 1.7.5 session skip as history only. Display/Recorder are repository hosts plus tagged Release zips, not NuGet / AOT CLI.
