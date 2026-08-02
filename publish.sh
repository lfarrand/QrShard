#!/usr/bin/env bash
# Publishes self-contained single-file QrShard binaries for every supported platform
# into publish/<rid>/. Run from the repository root:  bash ./publish.sh  [rid ...]
set -euo pipefail

rids=("$@")
if [ ${#rids[@]} -eq 0 ]; then
    rids=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

mkdir -p publish
publish_lock="publish/.qrshard-publish.lock"
lock_owner="$publish_lock/owner"
lock_token="$(LC_ALL=C od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
lock_acquired=0
current_stage=""
current_backup=""
current_target=""
cleanup() {
    if [ -n "$current_backup" ] && [ -d "$current_backup" ]; then
        if [ -n "$current_target" ] && [ ! -e "$current_target" ]; then
            mv -- "$current_backup" "$current_target" ||
                printf 'WARNING: restore failed; previous publish remains at %s\n' "$current_backup" >&2
        elif [ -n "$current_target" ] && [ -e "$current_target" ]; then
            rm -rf -- "$current_backup"
        fi
    fi
    if [ -n "$current_stage" ] && [ -d "$current_stage" ]; then
        rm -rf -- "$current_stage"
    fi
    if [ "$lock_acquired" -eq 1 ] && [ -d "$publish_lock" ]; then
        recorded_token="$(sed -n 's/^token=//p' "$lock_owner" 2>/dev/null || true)"
        if [ "$recorded_token" = "$lock_token" ]; then
            rm -f -- "$lock_owner"
            rmdir -- "$publish_lock" ||
                printf 'WARNING: could not remove empty publish lock %s\n' "$publish_lock" >&2
        else
            printf 'WARNING: could not verify ownership of publish lock %s; leaving it in place\n' \
                "$publish_lock" >&2
        fi
    fi
}
trap cleanup EXIT

# mkdir is the portable cross-process compare-and-set. A stale-looking lock is deliberately not
# reclaimed automatically: PID reuse and delayed publishers make that guess unsafe.
if ! mkdir -- "$publish_lock" 2>/dev/null; then
    printf 'Another publisher holds %s.\n' "$publish_lock" >&2
    if [ -f "$lock_owner" ]; then
        printf 'Owner metadata:\n' >&2
        # Do not let a hostile/stale lock file inject terminal control sequences.
        LC_ALL=C tr -cd '\11\12\15\40-\176' < "$lock_owner" |
            head -c 1000 | sed 's/^/  /' >&2 || true
        printf '\n' >&2
    fi
    printf 'If it terminated abnormally, verify that no publisher is running before removing the lock directory manually.\n' >&2
    exit 3
fi
lock_acquired=1
{
    printf 'token=%s\n' "$lock_token"
    printf 'pid=%s\n' "$$"
    printf 'host=%s\n' "$(hostname 2>/dev/null || printf unknown)"
    printf 'started_utc=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
} > "$lock_owner"

for rid in "${rids[@]}"; do
    case "$rid" in
        win-x64|linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
        *) printf 'Unsupported RID: %s\n' "$rid" >&2; exit 2 ;;
    esac
    echo "==> $rid"
    current_stage="$(mktemp -d "publish/.${rid}.tmp.XXXXXX")"
    dotnet publish src/QrShard -c Release -r "$rid" --self-contained \
        -p:PublishSingleFile=true -o "$current_stage"
    current_target="publish/$rid"
    current_backup=""
    if [ -e "$current_target" ]; then
        current_backup="$(mktemp -d "publish/.${rid}.backup.XXXXXX")"
        rmdir -- "$current_backup"
        mv -- "$current_target" "$current_backup"
    fi
    if mv -- "$current_stage" "$current_target"; then
        :
    else
        status=$?
        if [ -n "$current_backup" ] && [ -d "$current_backup" ] && [ ! -e "$current_target" ]; then
            mv -- "$current_backup" "$current_target" || true
        fi
        exit "$status"
    fi
    current_stage=""
    if [ -n "$current_backup" ]; then
        rm -rf -- "$current_backup"
    fi
    current_backup=""
    current_target=""
done

echo
echo "Published:"
for rid in "${rids[@]}"; do
    exe="publish/$rid/QrShard"
    [ -f "$exe.exe" ] && exe="$exe.exe"
    bytes="$(stat -c%s "$exe" 2>/dev/null || stat -f%z "$exe")"
    mb="$(awk -v bytes="$bytes" 'BEGIN { printf "%.1f", bytes / 1048576 }')"
    printf "%-12s %8s MB   %s\n" "$rid" "$mb" "$exe"
done
