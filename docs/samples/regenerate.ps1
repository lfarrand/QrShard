<#
.SYNOPSIS
  Regenerates the sample shard images shown in the README's "Sample output" section.

.DESCRIPTION
  Encodes a deterministic payload at each documented configuration, then derives two views
  per shard into this folder:
    <name>-full.png    the whole image, scaled down to fit the page (does NOT decode)
    <name>-detail.png  an exact 150x150 pixel region, magnified 3x with no resampling

  Full-resolution shards are far too large to commit (a filled Max4K image is ~25 MB of
  essentially incompressible pixels), which is why only the derived views live here.

  The payload is generated from a fixed seed, so the data field is reproducible. The images are
  NOT byte-identical between runs: every encode stamps a random 64-bit file id (ShardEncoder),
  which is what lets shards of different files share a folder without being confused for each
  other. That id lives in the metadata strip, so re-running changes the strip and leaves the
  data field alone — expect a small diff on the *-full.png views if you commit a regeneration.

.NOTES
  Windows only (uses System.Drawing for the image derivation).
#>
[CmdletBinding()]
param(
    # Width of the scaled whole-image view.
    [int] $FullWidth = 380,
    # Side length of the 1:1 region sampled for the detail view, and its magnification.
    [int] $CropPx = 150,
    [int] $Magnify = 3
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
$work     = Join-Path ([System.IO.Path]::GetTempPath()) "qrshard-samples-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Each configuration is encoded at a payload sized just under its per-image capacity, so the
# grid is genuinely full — a mostly-empty shard would misrepresent what the encoder emits.
$configs = @(
    @{ Name = 'default'; Bytes = 205000;  Args = @('-r','2160','-c','3','-b','4') }
    @{ Name = 'dense';   Bytes = 700000;  Args = @('-r','2160','-c','2','-b','6') }
    @{ Name = 'max4k';   Bytes = 4800000; Args = @('-r','3840x2160','-c','1','-b','6') }
    @{ Name = 'camera';  Bytes = 15800;   Args = @('-r','3840x2160','--camera') }
    @{ Name = 'mono';    Bytes = 7100;    Args = @('-r','2160','-c','8','-b','1') }
)

# Deterministic, incompressible-looking payload. A seeded System.Random keeps the data field
# stable between regenerations (the seeded sequence is stable across .NET versions), and unlike
# a scripted PRNG it fills the 4.8 MB Max4K payload instantly.
function New-Payload([int] $count, [string] $path) {
    $bytes = New-Object byte[] $count
    (New-Object System.Random 20260723).NextBytes($bytes)
    [System.IO.File]::WriteAllBytes($path, $bytes)
}

# Both views use nearest-neighbour on purpose. Any averaging filter (bicubic, or a proper box
# filter) is the "correct" way to minify, but here it destroys the subject: reducing 1 px cells
# by 10x averages 64 random palette colours per output pixel, and a Max4K shard collapses into
# flat grey. Point sampling keeps a real subset of cells, so the scaled view still reads as the
# dense colour field it actually is. It is also the only GDI+ mode that is bit-reproducible.
function Save-Scaled([System.Drawing.Image] $img, [int] $w, [int] $h, $mode, [string] $path) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = $mode
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $g.DrawImage($img, 0, 0, $w, $h)
        } finally { $g.Dispose() }
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }
}

try {
    foreach ($cfg in $configs) {
        $payload = Join-Path $work "$($cfg.Name).bin"
        New-Payload $cfg.Bytes $payload

        $shardDir = Join-Path $work $cfg.Name
        & dotnet run --project (Join-Path $repoRoot 'src\QrShard') -c Release -- `
            encode $payload -o $shardDir @($cfg.Args) | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { throw "encode failed for $($cfg.Name)" }

        $src = Get-ChildItem $shardDir -Filter *.png | Select-Object -First 1
        $img = [System.Drawing.Image]::FromFile($src.FullName)
        try {
            $h = [int][Math]::Round($FullWidth * $img.Height / $img.Width)
            Save-Scaled $img $FullWidth $h `
                ([System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor) `
                (Join-Path $here "$($cfg.Name)-full.png")

            # Centre crop at 1:1, then magnify nearest-neighbour so cell edges stay exact.
            $crop = New-Object System.Drawing.Bitmap($CropPx, $CropPx)
            try {
                $g = [System.Drawing.Graphics]::FromImage($crop)
                try {
                    $g.DrawImage($img,
                        (New-Object System.Drawing.Rectangle(0, 0, $CropPx, $CropPx)),
                        (New-Object System.Drawing.Rectangle(
                            [int](($img.Width - $CropPx) / 2), [int](($img.Height - $CropPx) / 2),
                            $CropPx, $CropPx)),
                        [System.Drawing.GraphicsUnit]::Pixel)
                } finally { $g.Dispose() }
                Save-Scaled $crop ($CropPx * $Magnify) ($CropPx * $Magnify) `
                    ([System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor) `
                    (Join-Path $here "$($cfg.Name)-detail.png")
            } finally { $crop.Dispose() }

            "{0,-8} {1}x{2} -> {3}-full.png, {3}-detail.png" -f $cfg.Name, $img.Width, $img.Height, $cfg.Name
        } finally { $img.Dispose() }
    }
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

''
'Sample images written to {0}' -f $here
Get-ChildItem $here -Filter *.png | Sort-Object Name |
    ForEach-Object { '  {0,-22} {1,9:N0} bytes' -f $_.Name, $_.Length }
