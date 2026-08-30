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

Leave `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.DependencyInjection.Abstractions`, and `System.IO.Hashing` at **10.0.10** until a single PR updates `src/QrShard` csproj + lock, both test/benchmark locks (`QrShard.Tool` project deps), `release.yml` SPDX `versionInfo` assertions, and `THIRD-PARTY-NOTICES.md`. Native AOT / ILLink stay **10.0.11**.

## Test-only Extensions

Bumps under `tests/QrShard.Tests` (Binder, EnvironmentVariables, Options.ConfigurationExtensions) stay out of product locks. Refresh the matching `packages.lock.json` in the same PR.

## ADS restore

Non-required Automatic Dependency Submission `submit-nuget` fails on Linux restore of `QrShard.Recorder.csproj` (`NETSDK1100`). It does not block merge.
