#!/usr/bin/env bash
# Publishes self-contained single-file QrShard binaries for every supported platform
# into publish/<rid>/. Run from the repository root:  ./publish.sh  [rid ...]
set -euo pipefail

rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
    rids=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

for rid in "${rids[@]}"; do
    echo "==> $rid"
    dotnet publish src/QrShard -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -p:InvariantGlobalization=true \
        -o "publish/$rid"
done

echo
echo "Published:"
for rid in "${rids[@]}"; do
    exe="publish/$rid/QrShard"
    [ -f "$exe.exe" ] && exe="$exe.exe"
    printf "%-12s %8.1f MB   %s\n" "$rid" "$(echo "$(stat -c%s "$exe" 2>/dev/null || stat -f%z "$exe") / 1048576" | bc -l)" "$exe"
done
