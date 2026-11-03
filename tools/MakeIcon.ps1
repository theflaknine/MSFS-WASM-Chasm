# Draws the app icon and writes src\WasmChasm.App\Assets\WasmChasm.ico.
# A small CRT monitor with an amber chasm on its screen and a block falling in.
# Each size is drawn separately, with less detail at small sizes so it stays legible.
#
#   powershell -ExecutionPolicy Bypass -File tools\MakeIcon.ps1 [preview.png]

param([string]$Preview)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$amber = [System.Drawing.Color]::FromArgb(255, 255, 176, 0)
$amberDim = [System.Drawing.Color]::FromArgb(255, 150, 100, 0)
$screen = [System.Drawing.Color]::FromArgb(255, 14, 11, 7)
$bezelLight = [System.Drawing.Color]::FromArgb(255, 74, 67, 57)
$bezelDark = [System.Drawing.Color]::FromArgb(255, 36, 33, 28)

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $crisp = $size -le 32
    $g.SmoothingMode = if ($crisp) { 'None' } else { 'AntiAlias' }
    $g.PixelOffsetMode = 'Half'

    $u = $size / 32.0

    # The monitor body.
    $body = New-RoundRect 0 0 ($size - 1) ($size - 1) ([Math]::Max(1, 5 * $u))
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0, 0, $size, $size), $bezelLight, $bezelDark, 90.0)
    $g.FillPath($bodyBrush, $body)

    # The glass.
    $inset = [Math]::Max(2, [Math]::Round(3 * $u))
    $sx = $inset; $sy = $inset; $sw = $size - 2 * $inset - 1; $sh = $size - 2 * $inset - 1 - [Math]::Round(2 * $u)
    $glass = New-RoundRect $sx $sy $sw $sh ([Math]::Max(1, 3 * $u))
    $g.FillPath((New-Object System.Drawing.SolidBrush $screen), $glass)
    $g.SetClip($glass)

    # The chasm, in proportions of the glass.
    $line = [single][Math]::Max(1, [Math]::Round(1.4 * $u))
    $pen = New-Object System.Drawing.Pen $amber, $line
    $groundY = $sy + $sh * 0.40
    $cx = $sx + $sw / 2.0
    $mouth = $sw * 0.27
    $floor = $sw * 0.08
    $bottomY = $sy + $sh * 0.92

    $g.DrawLine($pen, [single]$sx, [single]$groundY, [single]($cx - $mouth), [single]$groundY)
    $g.DrawLine($pen, [single]($cx + $mouth), [single]$groundY, [single]($sx + $sw), [single]$groundY)
    $g.DrawLine($pen, [single]($cx - $mouth), [single]$groundY, [single]($cx - $floor), [single]$bottomY)
    $g.DrawLine($pen, [single]($cx + $mouth), [single]$groundY, [single]($cx + $floor), [single]$bottomY)

    # Rungs receding into the dark, only where there's room for them.
    $rungs = if ($size -ge 64) { 4 } elseif ($size -ge 32) { 2 } elseif ($size -ge 24) { 1 } else { 0 }
    $rungPen = New-Object System.Drawing.Pen $amberDim, ([single][Math]::Max(1, [Math]::Round(0.8 * $u)))
    for ($i = 1; $i -le $rungs; $i++) {
        $t = 1 - [Math]::Pow(0.62, $i)
        $y = $groundY + ($bottomY - $groundY) * $t
        $half = $mouth + ($floor - $mouth) * $t
        $g.DrawLine($rungPen, [single]($cx - $half), [single]$y, [single]($cx + $half), [single]$y)
    }

    # The block, falling in.
    $block = [Math]::Max(2, [Math]::Round(3.6 * $u))
    $bx = [Math]::Round($cx - $block / 2.0)
    $by = [Math]::Round($sy + $sh * 0.13)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $amber), [single]$bx, [single]$by, [single]$block, [single]$block)

    # Faint scanlines at the sizes where they read as texture rather than noise.
    if ($size -ge 48) {
        $scan = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0, 0, 0)), ([single]($size / 128.0))
        $step = $size / 42.0
        for ($y = $sy; $y -lt ($sy + $sh); $y += $step) { $g.DrawLine($scan, [single]$sx, [single]$y, [single]($sx + $sw), [single]$y) }
    }

    $g.ResetClip()
    $g.Dispose()
    return $bmp
}

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\WasmChasm.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$out = Join-Path $assets 'WasmChasm.ico'

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Draw-Icon $s) }

$frames = foreach ($img in $images) {
    $ms = New-Object System.IO.MemoryStream
    $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    ,($ms.ToArray())
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f) }
$w.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes)"

if ($Preview) {
    # Every size, enlarged without smoothing, on a mid grey like a taskbar.
    $sheet = New-Object System.Drawing.Bitmap 1120, 560
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 70, 70, 76))
    $g.InterpolationMode = 'NearestNeighbor'
    $g.PixelOffsetMode = 'Half'
    $font = New-Object System.Drawing.Font 'Consolas', 11
    $pos = @(@(16, 8, 20, 300), @(20, 8, 170, 300), @(24, 8, 350, 300), @(32, 6, 570, 300), @(48, 4, 800, 300), @(256, 1, 20, 10), @(128, 2, 300, 10), @(64, 3, 590, 10))
    foreach ($p in $pos) {
        $img = $images[[array]::IndexOf($sizes, $p[0])]
        $g.DrawImage($img, $p[2], $p[3], $p[0] * $p[1], $p[0] * $p[1])
        $g.DrawString("$($p[0])", $font, [System.Drawing.Brushes]::White, $p[2], ($p[3] + $p[0] * $p[1] + 2))
    }
    # True size, as it appears in the taskbar and title bar.
    $x = 860; foreach ($s in 16, 24, 32, 48) { $g.DrawImage($images[[array]::IndexOf($sizes, $s)], $x, 60, $s, $s); $x += $s + 14 }
    $g.DrawString('actual size', $font, [System.Drawing.Brushes]::White, 860, 120)
    $g.Dispose()
    $sheet.Save($Preview)
}
