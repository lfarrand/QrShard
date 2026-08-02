# Publishes self-contained single-file QrShard binaries for every supported platform
# into publish/<rid>/. Run from the repository root:  ./publish.ps1  [-Rids win-x64,linux-x64]
param(
    [string[]]$Rids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

$ErrorActionPreference = "Stop"
$allowedRids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
$publishRoot = Join-Path (Get-Location) "publish"
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
$lockPath = Join-Path $publishRoot ".qrshard-publish.lock"
$lockOwnerPath = Join-Path $lockPath "owner"
$lockToken = [Guid]::NewGuid().ToString("N")
$lockAcquired = $false

try {
    try {
        # Directory creation is the cross-process compare-and-set. Do not use -Force: an existing
        # lock must fail before any RID output is staged or replaced.
        New-Item -ItemType Directory -Path $lockPath -ErrorAction Stop | Out-Null
        $lockAcquired = $true
    }
    catch {
        $details = ""
        if (Test-Path -LiteralPath $lockOwnerPath) {
            try {
                $rawDetails = Get-Content -Raw -LiteralPath $lockOwnerPath
                $safeDetails = [Regex]::Replace($rawDetails, '[^\x09\x0A\x0D\x20-\x7E]', '?')
                if ($safeDetails.Length -gt 1000) { $safeDetails = $safeDetails.Substring(0, 1000) }
                $details = "`nOwner metadata: $safeDetails"
            }
            catch { $details = "`nOwner metadata could not be read." }
        }
        throw "Another publisher holds '$lockPath'.$details`nIf it terminated abnormally, verify that no publisher is running before removing the lock directory manually."
    }

    @(
        "token=$lockToken"
        "pid=$PID"
        "host=$([Environment]::MachineName)"
        "started_utc=$([DateTimeOffset]::UtcNow.ToString('O'))"
    ) | Set-Content -LiteralPath $lockOwnerPath -Encoding utf8

    foreach ($rid in $Rids) {
        if ($rid -notin $allowedRids) { throw "Unsupported RID: $rid" }
        Write-Host "==> $rid"
        $stage = Join-Path $publishRoot ".$rid.tmp.$([Guid]::NewGuid().ToString('N'))"
        $target = Join-Path $publishRoot $rid
        $backup = $null
        $installed = $false
        New-Item -ItemType Directory -Path $stage | Out-Null
        try {
            dotnet publish src/QrShard -c Release -r $rid --self-contained `
                -p:PublishSingleFile=true -o $stage
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }

            if (Test-Path -LiteralPath $target) {
                $backup = Join-Path $publishRoot ".$rid.backup.$([Guid]::NewGuid().ToString('N'))"
                Move-Item -LiteralPath $target -Destination $backup
            }
            try {
                Move-Item -LiteralPath $stage -Destination $target
                $installed = $true
            }
            catch {
                if ($null -ne $backup -and (Test-Path -LiteralPath $backup) -and
                    -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $backup -Destination $target
                }
                throw
            }
            if ($null -ne $backup -and (Test-Path -LiteralPath $backup)) {
                Remove-Item -LiteralPath $backup -Recurse -Force
            }
        }
        finally {
            if (Test-Path -LiteralPath $stage) {
                Remove-Item -LiteralPath $stage -Recurse -Force
            }
            if (-not $installed -and $null -ne $backup -and (Test-Path -LiteralPath $backup) -and
                -not (Test-Path -LiteralPath $target)) {
                Move-Item -LiteralPath $backup -Destination $target
            }
        }
    }

    Write-Host ""
    Write-Host "Published:"
    foreach ($rid in $Rids) {
        $exe = Get-ChildItem "publish/$rid" -File | Where-Object { $_.Name -match '^QrShard(\.exe)?$' }
        "{0,-12} {1,8:N1} MB   {2}" -f $rid, ($exe.Length / 1MB), $exe.FullName | Write-Host
    }
}
finally {
    if ($lockAcquired -and (Test-Path -LiteralPath $lockPath)) {
        $ownsLock = $false
        try {
            $recordedToken = Get-Content -LiteralPath $lockOwnerPath |
                Where-Object { $_.StartsWith("token=", [StringComparison]::Ordinal) } |
                Select-Object -First 1
            $ownsLock = $recordedToken -eq "token=$lockToken"
        }
        catch {
            Write-Warning "Could not verify ownership of publish lock '$lockPath'; leaving it in place."
        }

        if ($ownsLock) {
            # Non-recursive removal ensures unexpected content is never erased as lock cleanup.
            Remove-Item -LiteralPath $lockOwnerPath -Force
            try { Remove-Item -LiteralPath $lockPath -ErrorAction Stop }
            catch { Write-Warning "Could not remove empty publish lock '$lockPath': $_" }
        }
    }
}
