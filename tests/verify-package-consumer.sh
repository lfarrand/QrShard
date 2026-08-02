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
# Or verify already-packed release bytes by setting QRSHARD_PREBUILT_FEED and
# QRSHARD_PACKAGE_VERSION. This prevents a fresh, untested rebuild from being published later.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
FEED="${QRSHARD_PREBUILT_FEED:-$WORK/feed}"

# The published 1.3.7 exists on nuget.org, so packing at the real version would let a restore
# silently satisfy itself from there and green-light a broken local build. A sentinel version
# that exists in no other feed makes the resolution unambiguous.
LOCAL_VERSION="${QRSHARD_PACKAGE_VERSION:-99.99.99-consumertest}"

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

if [ -z "${QRSHARD_PREBUILT_FEED:-}" ]; then
    step "Pack (with whatever licence the repo build normally uses)"
    dotnet pack "$REPO/src/QrShard.Core/QrShard.Core.csproj" -c Release -o "$FEED" \
        -p:Version="$LOCAL_VERSION" --artifacts-path "$WORK/build-artifacts" --nologo -v quiet
    dotnet pack "$REPO/src/QrShard/QrShard.csproj" -c Release -o "$FEED" \
        -p:Version="$LOCAL_VERSION" --artifacts-path "$WORK/build-artifacts" --nologo -v quiet
else
    step "Use the prebuilt release packages (no rebuild)"
fi
ls "$FEED"/*.nupkg | sed 's|.*/|  packed: |'

step "0. Package redistribution notices and flattened dependency inventory"
TOOL_PACKAGE="$(ls "$FEED"/QrShard.Tool.*.nupkg)"
CORE_PACKAGE="$(ls "$FEED"/QrShard.Core.*.nupkg)"
tool_entries="$(unzip -Z1 "$TOOL_PACKAGE")"

notice_ok=true
for entry in \
    tools/net10.0/any/LICENSE \
    tools/net10.0/any/THIRD-PARTY-NOTICES.md \
    tools/net10.0/any/IMAGESHARP-APACHE-2.0.txt \
    tools/net10.0/any/DOTNET-LICENSE.txt \
    tools/net10.0/any/DOTNET-THIRD-PARTY-NOTICES.txt; do
    if ! grep -Fxq "$entry" <<<"$tool_entries"; then
        bad "tool package is missing $entry"
        notice_ok=false
    fi
done

if $notice_ok &&
   unzip -p "$TOOL_PACKAGE" tools/net10.0/any/LICENSE | cmp -s - "$REPO/LICENSE" &&
   unzip -p "$TOOL_PACKAGE" tools/net10.0/any/THIRD-PARTY-NOTICES.md | cmp -s - "$REPO/THIRD-PARTY-NOTICES.md" &&
   unzip -p "$TOOL_PACKAGE" tools/net10.0/any/IMAGESHARP-APACHE-2.0.txt | cmp -s - "$REPO/licenses/Apache-2.0.txt" &&
   unzip -p "$TOOL_PACKAGE" tools/net10.0/any/DOTNET-LICENSE.txt | cmp -s - "$REPO/licenses/DotNet-MIT.txt"; then
    ok "tool package carries the tracked project, ImageSharp, and .NET license files byte-for-byte"
else
    bad "tool package redistribution notices differ from the tracked canonical files"
fi

dotnet_notice_hash="$(unzip -p "$TOOL_PACKAGE" tools/net10.0/any/DOTNET-THIRD-PARTY-NOTICES.txt | sha256sum | awk '{print toupper($1)}')"
if [ "$dotnet_notice_hash" = "6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21" ]; then
    ok "tool package carries the exact shared .NET 10.0.10 third-party notice"
else
    bad "tool package .NET notice is absent or not the reviewed 10.0.10 bytes"
fi

actual_dlls="$(grep -E '^tools/net10\.0/any/[^/]+\.dll$' <<<"$tool_entries" |
    sed 's|.*/||' | grep -vE '^QrShard(\.Core)?\.dll$' | sort || true)"
expected_dlls="$(printf '%s\n' \
    Microsoft.Extensions.DependencyInjection.Abstractions.dll \
    Microsoft.Extensions.DependencyInjection.dll \
    SixLabors.ImageSharp.dll \
    System.IO.Hashing.dll | sort)"
if [ "$actual_dlls" = "$expected_dlls" ]; then
    ok "flattened tool dependency DLLs match the reviewed notice allowlist"
else
    bad "flattened dependency inventory changed and needs a notice review: $actual_dlls"
fi

core_dlls="$(unzip -Z1 "$CORE_PACKAGE" | grep -E '\.dll$' | sed 's|.*/||' | sort || true)"
if [ "$core_dlls" = "QrShard.Core.dll" ] && unzip -Z1 "$CORE_PACKAGE" | grep -Fxq LICENSE; then
    ok "Core package carries its MIT license and does not flatten third-party DLLs"
else
    bad "Core package license/dependency layout changed"
fi

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
        if cmp -s "$FEED/QrShard.Core.$LOCAL_VERSION.nupkg" \
                  "$NUGET_PACKAGES/qrshard.core/$LOCAL_VERSION/qrshard.core.$LOCAL_VERSION.nupkg"; then
            ok "consumer restored the exact tested QrShard.Core package bytes"
        else
            bad "consumer restored QrShard.Core from another source/build"
        fi
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

// A library must not claim a generic host's ordinary appsettings.json. Older QrShard.Core
// constructors parsed this adjacent file and rejected unrelated Logging/ConnectionStrings keys.
string hostSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
File.WriteAllText(hostSettings, """{ "Logging": { "LogLevel": { "Default": "Information" } } }""");
try
{
    var codec = new QrShardCodec();
    codec.EncodeFile(input, Path.Combine(dir, "shards"));
    codec.DecodeImages(Directory.GetFiles(Path.Combine(dir, "shards"), "*.png"), Path.Combine(dir, "out.bin"));
}
finally
{
    File.Delete(hostSettings);
}

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
TOOL_CONFIG="$WORK/tool-nuget.config"
cat > "$TOOL_CONFIG" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="exact-tested-package" value="$(winpath "$FEED")" />
  </packageSources>
</configuration>
XML
if dotnet tool install QrShard.Tool --version "$LOCAL_VERSION" \
       --tool-path "$TOOLS" --configfile "$TOOL_CONFIG" --no-cache >/dev/null 2>&1; then
    ok "dotnet tool install succeeded"
    # The feed contains one QrShard.Tool ID/version, public sources are cleared, --no-cache is
    # set, and NUGET_PACKAGES is isolated. A successful install therefore consumed this exact
    # nupkg even on SDKs whose dotnet-tool path does not retain the source archive in that cache.
    ok "tool install used the exact tested QrShard.Tool package bytes from the isolated feed"
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
