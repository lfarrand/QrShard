#!/usr/bin/env bash
#
# Consumer-contract test: builds the NuGet packages and uses them the way a stranger would,
# from outside the repository.
#
# The unit suite references the projects directly, so it never exercises the packaged public
# surface — which is how a QrShard.Core readme sample that did not compile (wrong namespace,
# static calls to instance methods) shipped in 1.3.5, 1.3.6 and 1.3.7 without a single test
# failing. This script closes that gap:
#
#   1. the csharp sample in PACKAGE.md compiles verbatim against the packed package
#   2. that public API actually round-trips a file
#   3. QrShard.Tool installs as a dotnet tool and passes its own self-test
#
# All three run with no Six Labors licence present, which also pins the claim both readmes make:
# ImageSharp's build-time licence check lives in its build/ folder rather than buildTransitive/,
# so it does not reach projects that merely consume QrShard.
#
# Usage: tests/verify-package-consumer.sh
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
FEED="$WORK/feed"

# The published 1.3.7 exists on nuget.org, so packing at the real version would let a restore
# silently satisfy itself from there and green-light a broken local build. A sentinel version
# that exists in no other feed makes the resolution unambiguous.
LOCAL_VERSION="99.99.99-consumertest"

PASS=0
FAIL=0

cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

step()  { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }
ok()    { printf '  PASS  %s\n' "$1"; PASS=$((PASS + 1)); }
bad()   { printf '  FAIL  %s\n' "$1"; FAIL=$((FAIL + 1)); }

# Under Git Bash the shell's /tmp/... is not what a Windows dotnet.exe resolves (it reads it as
# C:\tmp\...). Command-line arguments get translated for us; paths written into files or exported
# as environment variables do not, so those are converted explicitly.
winpath() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Isolated package cache: the consumer must resolve from the feed built here, never from
# whatever a previous run happened to leave in the developer's global cache.
export NUGET_PACKAGES="$(winpath "$WORK/packages")"

step "Pack (with whatever licence the repo build normally uses)"
dotnet pack "$REPO/src/QrShard.Core/QrShard.Core.csproj" -c Release -o "$FEED" \
    -p:Version="$LOCAL_VERSION" --nologo -v quiet
dotnet pack "$REPO/src/QrShard/QrShard.csproj" -c Release -o "$FEED" \
    -p:Version="$LOCAL_VERSION" --nologo -v quiet
ls "$FEED"/*.nupkg | sed 's|.*/|  packed: |'

# Everything past this point is the consumer's world: no licence file, no licence key.
unset SixLaborsLicenseKey || true

step "1. The PACKAGE.md sample compiles verbatim"
APP="$WORK/consumer"
dotnet new console -o "$APP" --force >/dev/null
cat > "$APP/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$(winpath "$FEED")" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
dotnet add "$APP" package QrShard.Core --version "$LOCAL_VERSION" --no-restore >/dev/null

# The sample is lifted straight out of the shipped readme — no transcription, so the thing
# under test is the text a consumer actually reads.
awk '/^```csharp$/{f=1;next} /^```$/{f=0} f' "$REPO/src/QrShard.Core/PACKAGE.md" > "$APP/Program.cs"
if [ ! -s "$APP/Program.cs" ]; then
    bad "no csharp block found in PACKAGE.md (did the fence change?)"
else
    # Compile only: the sample names holiday-photos.zip, which is illustrative and absent.
    if dotnet build "$APP" -c Release --nologo -v quiet > "$WORK/build.log" 2>&1; then
        ok "documented sample compiles against the packed QrShard.Core"
        # Only meaningful when the build actually ran to completion — asserting it on a build
        # that failed for some other reason would report a pass for the wrong reason.
        if grep -qiE "sixlabors|licen[cs]e" "$WORK/build.log"; then
            bad "consumer build raised a Six Labors licence requirement"
        else
            ok "no Six Labors licence requirement reached the consumer build"
        fi
    else
        bad "documented sample does NOT compile:"
        sed 's/^/        /' "$WORK/build.log" | tail -15
    fi
fi

step "2. The public API round-trips a file"
cat > "$APP/Program.cs" <<'CS'
using QrShard;

string dir = Path.Combine(Path.GetTempPath(), "qrshard-consumer-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
string input = Path.Combine(dir, "payload.bin");
var data = new byte[120_000];
new Random(7).NextBytes(data);
File.WriteAllBytes(input, data);

var codec = new QrShardCodec();
codec.EncodeFile(input, Path.Combine(dir, "shards"));
codec.DecodeImages(Directory.GetFiles(Path.Combine(dir, "shards"), "*.png"), Path.Combine(dir, "out.bin"));

bool identical = File.ReadAllBytes(input).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "out.bin")));
Directory.Delete(dir, true);
Console.WriteLine(identical ? "ROUNDTRIP-OK" : "ROUNDTRIP-MISMATCH");
return identical ? 0 : 1;
CS
if dotnet run --project "$APP" -c Release --nologo -v quiet 2>&1 | grep -q "ROUNDTRIP-OK"; then
    ok "encode/decode via the public API is bit-identical"
else
    bad "public API round trip failed"
fi

step "3. QrShard.Tool installs and self-tests"
TOOLS="$WORK/tools"
if dotnet tool install QrShard.Tool --version "$LOCAL_VERSION" \
       --tool-path "$TOOLS" --add-source "$FEED" >/dev/null 2>&1; then
    ok "dotnet tool install succeeded"
    if "$TOOLS/qrshard" test 2>&1 | grep -q "All self-tests passed"; then
        ok "installed tool passes its own self-test"
    else
        bad "installed tool failed its self-test"
    fi
else
    bad "dotnet tool install failed"
fi

printf '\n\033[1m%d passed, %d failed\033[0m\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]
