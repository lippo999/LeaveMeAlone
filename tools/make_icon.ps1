<#
  Builds the app icon from a photo: crops a square, rounds the corners and writes
    src/LeaveMeAlone/Assets/app.ico  (16-256 px PNG frames; exe, window and tray icon)
    src/LeaveMeAlone/Assets/app.png  (256 px; sidebar logo)
  The source photo itself is not committed (it is ~10 MB); only these small outputs are.

  Usage (Windows PowerShell):
    powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1 -Source C:\path\LeaveMeAloneIcon.png
  -CropX/-CropY/-CropSize pick the square in source pixels (defaults frame the cat's face).
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [int]$CropX = 920,
    [int]$CropY = 100,
    [int]$CropSize = 1520
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$assets = Join-Path $PSScriptRoot '..\src\LeaveMeAlone\Assets'

function New-Tile([System.Drawing.Image]$photo, [int]$size) {
    # Scale the crop first, then paint it through an anti-aliased rounded-square path.
    $scaled = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($photo, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $CropX, $CropY, $CropSize, $CropSize, 'Pixel')
    $g.Dispose()

    $tile = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($tile)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $r = [Math]::Max(2.0, $size * 0.22)
    $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.TextureBrush $scaled
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose(); $g.Dispose(); $scaled.Dispose()
    return $tile
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$photo = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    $frames = foreach ($s in $sizes) {
        $tile = New-Tile $photo $s
        if ($s -eq 256) { $tile.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        , (Get-PngBytes $tile)
        $tile.Dispose()
    }
} finally { $photo.Dispose() }

# ICO container: header, one 16-byte directory entry per frame, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$frames[$i].Length); $w.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $out.ToArray())
Get-ChildItem $assets | ForEach-Object { '{0}  {1:N0} KB' -f $_.Name, ($_.Length / 1KB) }
