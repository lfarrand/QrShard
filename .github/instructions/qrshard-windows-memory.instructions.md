---
description: Windows sidecar layout for QrShard Display and Recorder after absorbing RemoteDisplayCapture.
applyTo:
  - "src/QrShard.Display/**"
  - "src/QrShard.Recorder/**"
  - "src/QrShard.Windows.Logic/**"
  - "QrShard.Windows.slnx"
  - "src/QrShard/Cli.cs"
---

# QrShard Windows Memory

Keep DXGI/WPF hosts off the cross-platform CLI and solution.

## Sidecar layout

Put WPF Display and DXGI Recorder on `QrShard.Windows.slnx` with `net10.0-windows`. Put testable parse/termination/TIFF logic in `src/QrShard.Windows.Logic` (`net10.0`) on `QrShard.slnx` so Linux CI can run it.

## CLI boundary

Keep `Cli.ArgSpecs` free of `display` and `record` verbs. Call the hosts as `QrShard.Display <folder> [fps] [memory-cap] [once]` and `QrShard.Recorder <output-folder>`.

## Cross-platform path

Leave HTML `SlideshowWriter` and ffmpeg `receive --screen` as the non-Windows sender/capture path. Do not fold Desktop Duplication into `ScreenFrameSource`.

## Recorder pin

Pin `Vortice.Direct3D11` at 3.8.3 on `QrShard.Recorder` only. Provenance is RemoteDisplayCapture `8e8594f7`.

## Linux restore of Windows TFMs

Set `<EnableWindowsTargeting>true</EnableWindowsTargeting>` on Display and Recorder only (not `Directory.Build.props`). GitHub Automatic Dependency Submission restores every csproj on Linux and otherwise fails with NETSDK1100. Do not put `UseWPF` / `net10.0-windows` on `QrShard.slnx`.

## Documentation fence

Describe Display/Recorder as repository hosts plus tagged `qrshard-display-win-x64.zip` / `qrshard-recorder-win-x64.zip` assets. They are not NuGet packages and not inside Native-AOT CLI archives. Session PhotoFusion is shipped from 1.7.6; **1.7.5** skipped the in-process session path. Do not launch `QrShard.Display.exe` in CI (`MessageBox` hangs).
