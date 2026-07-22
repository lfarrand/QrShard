<#
.SYNOPSIS
  Regenerates the cross-version golden interop fixtures.

  For each released tag, this builds that version's CLI from a git worktree and encodes a
  matrix of payloads exercising the wire-format surface AVAILABLE AT THAT VERSION (compression,
  encryption, cross-shard parity, fountain coding, the camera profile, and — from v1.1.0 — the
  v2 permuted interleave). The resulting shard images are committed as frozen fixtures; the
  GoldenInteropTests then assert the CURRENT decoder still reconstructs every one, byte-for-byte.

  Fixtures are intentionally tiny (700px, few-KB payloads) to keep the repo light. Encryption
  and fountain fixtures bake their random salt / fileId into the committed shards, so decoding
  stays deterministic.

  Run from the repo root:  pwsh tests/QrShard.Tests/golden/regenerate.ps1
  Requires sixlabors.lic at the repo root (as for any build).
#>
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../../..").Path
$goldenRoot = $PSScriptRoot
$license = Join-Path $repo 'sixlabors.lic'
if (-not (Test-Path $license)) { throw "sixlabors.lic not found at repo root — required to build old tags." }

# tag -> which config keys it supports (interleave2 arrived in v1.1.0).
$versions = @(
    @{ Tag = 'v1.0.0'; Interleave2 = $false },
    @{ Tag = 'v1.1.0'; Interleave2 = $true  }
)

# Deterministic payloads (fixed bytes) so a regenerate only changes shards if the format did.
function New-Payload([int]$len, [int]$seed, [bool]$compressible) {
    $bytes = New-Object byte[] $len
    $rng = New-Object Random $seed
    if ($compressible) {
        $line = [Text.Encoding]::ASCII.GetBytes("The quick brown fox jumps over the lazy dog. 0123456789.`n")
        for ($i = 0; $i -lt $len; $i++) { $bytes[$i] = $line[$i % $line.Length] }
    } else {
        $rng.NextBytes($bytes)
    }
    return $bytes
}

foreach ($v in $versions) {
    $tag = $v.Tag
    $work = Join-Path $env:TEMP "qr-golden-$tag"
    if (Test-Path $work) { git -C $repo worktree remove --force $work 2>$null; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    git -C $repo worktree add $work $tag | Out-Null
    Copy-Item $license (Join-Path $work 'sixlabors.lic')
    dotnet build (Join-Path $work 'src/QrShard') -c Release --nologo -v q | Out-Null
    $exe = Get-ChildItem (Join-Path $work 'src/QrShard/bin/Release') -Recurse -Filter QrShard.dll | Select-Object -First 1

    # config key, extra args, payload spec, password (or $null)
    $configs = @(
        @{ Key = 'compressed'; Args = @();                      Len = 6000;  Seed = 1; Comp = $true;  Pw = $null },
        @{ Key = 'raw';        Args = @('--no-compress');       Len = 6000;  Seed = 2; Comp = $false; Pw = $null },
        @{ Key = 'parity';     Args = @('-R','25');             Len = 40000; Seed = 3; Comp = $false; Pw = $null },
        @{ Key = 'fountain';   Args = @('-F','50');             Len = 40000; Seed = 4; Comp = $false; Pw = $null },
        @{ Key = 'encrypted';  Args = @('-p','goldpw');         Len = 6000;  Seed = 5; Comp = $false; Pw = 'goldpw' },
        @{ Key = 'highecc';    Args = @('-e','48');             Len = 6000;  Seed = 6; Comp = $false; Pw = $null },
        @{ Key = 'camera';     Args = @('--camera');            Len = 2000;  Seed = 7; Comp = $false; Pw = $null }
    )
    if ($v.Interleave2) {
        $configs += @{ Key = 'interleave2'; Args = @('--interleave2'); Len = 40000; Seed = 8; Comp = $false; Pw = $null }
    }

    foreach ($c in $configs) {
        $dir = Join-Path (Join-Path $goldenRoot $tag) $c.Key
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory $dir | Out-Null

        $payload = New-Payload $c.Len $c.Seed $c.Comp
        $tmpIn = Join-Path $env:TEMP "goldin-$($c.Key).bin"
        [IO.File]::WriteAllBytes($tmpIn, $payload)
        $sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($payload)).Replace('-','').ToLower()

        $res = if ($c.Key -eq 'camera') { '1080' } else { '700' }
        dotnet $exe.FullName encode $tmpIn -o $dir -r $res @($c.Args) | Out-Null
        Remove-Item $tmpIn

        $manifest = [ordered]@{
            version = $tag; config = $c.Key
            expectedSha256 = $sha; expectedLength = $payload.Length
            password = $c.Pw
        }
        $manifest | ConvertTo-Json | Set-Content (Join-Path $dir 'manifest.json')
        Write-Host "  $tag/$($c.Key): $((Get-ChildItem $dir -Filter *.png).Count) image(s)"
    }

    git -C $repo worktree remove --force $work
}
Write-Host "Golden fixtures regenerated under $goldenRoot"
