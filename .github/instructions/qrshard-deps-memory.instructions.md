---
description: Dependabot and product inventory pins for QrShard lockfiles, CodeQL, and SPDX.
applyTo:
  - ".github/dependabot.yml"
  - ".github/workflows/codeql.yml"
  - ".github/workflows/release.yml"
  - "**/packages.lock.json"
  - "src/QrShard/QrShard.csproj"
  - "src/QrShard.Core/QrShard.Core.csproj"
---

# QrShard Dependency Memory

Keep product Hashing/DI inventory and CodeQL action pins consistent across lockfiles and release checks.

## CodeQL pair

Pin `github/codeql-action/init` and `github/codeql-action/analyze` to the same commit SHA. A split Dependabot pair fails required Analyze (`Loaded a configuration file for version '4.37.9', but running version '4.37.7'`). Group `github/codeql-action*` in Dependabot.

## Product DI / Hashing

Product `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.DependencyInjection.Abstractions` are **10.0.11**. `System.IO.Hashing` stays **10.0.10**. A product DI bump must update `src/QrShard` csproj + lock, both test/benchmark locks (`QrShard.Tool` project deps), `release.yml` SPDX `versionInfo` assertions, and `THIRD-PARTY-NOTICES.md` in the same PR. Native AOT / ILLink stay **10.0.11**.

## Coordinated product DI PRs

Dependabot PRs that only edit `src/QrShard` fail required CI: Tests/Benchmarks locks still record `QrShard.Tool` project deps as the old range (`NU1004` under `RestoreLockedMode`), and `release.yml` SPDX still asserts the previous DI `versionInfo`. Close those PRs and land one inventory change that also refreshes consumer locks and SPDX. Leave Hashing on its own pin.

## Test-only Extensions

Bumps under `tests/QrShard.Tests` (Binder, EnvironmentVariables, Options.ConfigurationExtensions) stay out of product locks. Refresh the matching `packages.lock.json` in the same PR.

## ADS restore

GitHub Automatic Dependency Submission `submit-nuget` restores every csproj on Linux. Display and Recorder keep `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so that restore does not raise NETSDK1100. Do not put that property in `Directory.Build.props`.
