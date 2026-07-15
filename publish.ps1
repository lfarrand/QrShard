# Publishes self-contained single-file QrShard binaries for every supported platform
# into publish/<rid>/. Run from the repository root:  ./publish.ps1  [-Rids win-x64,linux-x64]
param(
    [string[]]$Rids = @("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

$ErrorActionPreference = "Stop"
foreach ($rid in $Rids) {
    Write-Host "==> $rid"
    dotnet publish src/QrShard -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true -p:InvariantGlobalization=true `
        -o "publish/$rid"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "Published:"
foreach ($rid in $Rids) {
    $exe = Get-ChildItem "publish/$rid" -File | Where-Object { $_.Name -match '^QrShard(\.exe)?$' }
    "{0,-12} {1,8:N1} MB   {2}" -f $rid, ($exe.Length / 1MB), $exe.FullName | Write-Host
}
