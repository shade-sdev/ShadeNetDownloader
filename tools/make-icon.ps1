<#
.SYNOPSIS
  Draws the ShadeNet icon (blue rounded square, white download arrow) at several sizes and writes
  src/ShadeNetDownloader/Assets/app.ico (PNG-compressed entries, 16-256 px) plus the extension's PNG icons.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$assets = Join-Path $root "src\ShadeNetDownloader\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($size * 0.22); $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($size, $size)), ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.6, $size * 0.11))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $cx = [single]($size / 2.0)
    $g.DrawLine($pen, $cx, [single]($size * 0.24), $cx, [single]($size * 0.62))
    [System.Drawing.PointF[]]$head = @([System.Drawing.PointF]::new([single]($size * 0.31), [single]($size * 0.46)), [System.Drawing.PointF]::new($cx, [single]($size * 0.66)), [System.Drawing.PointF]::new([single]($size * 0.69), [single]($size * 0.46)))
    $g.DrawLines($pen, $head)
    $g.DrawLine($pen, [single]($size * 0.28), [single]($size * 0.78), [single]($size * 0.72), [single]($size * 0.78))
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

# ICO container: header, one directory entry per image, then the PNG data
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes((Join-Path $assets "app.ico"), $out.ToArray())

# the extension uses the same drawing
$extIcons = Join-Path $root "extension\chromium\icons"
foreach ($s in 16, 32, 48, 128) { [System.IO.File]::WriteAllBytes((Join-Path $extIcons "icon$s.png"), (New-IconPng $s)) }

"wrote $(Join-Path $assets 'app.ico') ($((Get-Item (Join-Path $assets 'app.ico')).Length) bytes) with sizes $($sizes -join ', ')"
