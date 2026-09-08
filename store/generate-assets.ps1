# Rebuild Store listing PNGs from Assets\app-icon-512.png
# Usage: powershell -ExecutionPolicy Bypass -File store\generate-assets.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$StoreRoot = $PSScriptRoot
$RepoRoot = Split-Path $StoreRoot -Parent
$IconPath = Join-Path $RepoRoot "Assets\app-icon-512.png"
$Logos = Join-Path $StoreRoot "logos"
$Shots = Join-Path $StoreRoot "screenshots"
$Promo = Join-Path $StoreRoot "promo"

if (-not (Test-Path $IconPath)) { throw "Missing $IconPath" }
foreach ($d in @($Logos, $Shots, $Promo)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

function Save-Png([System.Drawing.Bitmap]$bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host $path
}

function New-Font([string]$family, [single]$size, [System.Drawing.FontStyle]$style) {
    return New-Object -TypeName System.Drawing.Font -ArgumentList @($family, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

function Fill-RoundRect($g, $brush, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    if ($d -lt 2) {
        $g.FillRectangle($brush, $x, $y, $w, $h)
        return
    }
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function Draw-RoundRect($g, $pen, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.DrawPath($pen, $path)
    $path.Dispose()
}

function Prep-Graphics($g) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
}

$brandBg = [System.Drawing.Color]::FromArgb(255, 26, 31, 42)
$brandFg = [System.Drawing.Color]::FromArgb(255, 244, 247, 251)
$brandMuted = [System.Drawing.Color]::FromArgb(255, 154, 168, 189)
$accent = [System.Drawing.Color]::FromArgb(255, 47, 134, 232)
$icon = [System.Drawing.Image]::FromFile($IconPath)

function Draw-IconOn($g, [int]$x, [int]$y, [int]$size) {
    $g.DrawImage($icon, $x, $y, $size, $size)
}

# --- logos ---
function New-SquareLogo([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Prep-Graphics $g
    $g.Clear($brandBg)
    Draw-IconOn $g 0 0 $size
    Save-Png $bmp $path
    $g.Dispose(); $bmp.Dispose()
}

New-SquareLogo 300 (Join-Path $Logos "app-tile-300.png")

# 1:1 box art 1080 - title in top two-thirds
$box = New-Object System.Drawing.Bitmap 1080, 1080
$g = [System.Drawing.Graphics]::FromImage($box)
Prep-Graphics $g
$g.Clear($brandBg)
Draw-IconOn $g 250 90 580
$titleFont = New-Font "Segoe UI" 72 ([System.Drawing.FontStyle]::Bold)
$tagFont = New-Font "Segoe UI" 28 ([System.Drawing.FontStyle]::Regular)
$fgBrush = New-Object System.Drawing.SolidBrush $brandFg
$mutedBrush = New-Object System.Drawing.SolidBrush $brandMuted
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString("No Click Switch", $titleFont, $fgBrush, (New-Object System.Drawing.RectangleF 40, 700, 1000, 90), $sf)
$g.DrawString("Hover to switch  -  NCS", $tagFont, $mutedBrush, (New-Object System.Drawing.RectangleF 40, 790, 1000, 50), $sf)
Save-Png $box (Join-Path $Logos "box-art-1080.png")
$sf.Dispose(); $titleFont.Dispose(); $tagFont.Dispose()
$g.Dispose(); $box.Dispose()

# 2:3 poster 720x1080 - title in top two-thirds
$poster = New-Object System.Drawing.Bitmap 720, 1080
$g = [System.Drawing.Graphics]::FromImage($poster)
Prep-Graphics $g
$g.Clear($brandBg)
Draw-IconOn $g 140 80 440
$titleFont = New-Font "Segoe UI" 48 ([System.Drawing.FontStyle]::Bold)
$tagFont = New-Font "Segoe UI" 22 ([System.Drawing.FontStyle]::Regular)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString("No Click Switch", $titleFont, $fgBrush, (New-Object System.Drawing.RectangleF 24, 560, 672, 70), $sf)
$g.DrawString("Always-on-top window switcher", $tagFont, $mutedBrush, (New-Object System.Drawing.RectangleF 24, 630, 672, 40), $sf)
$chipFont = New-Font "Segoe UI" 16 ([System.Drawing.FontStyle]::Regular)
$chipBg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 35, 48))
$chips = @("Hover to switch", "One tab per window", "CPU  MEM  Temps")
$cy = 700
foreach ($c in $chips) {
    $sz = $g.MeasureString($c, $chipFont)
    $cw = [int]$sz.Width + 28
    $cx = [int]((720 - $cw) / 2)
    Fill-RoundRect $g $chipBg $cx $cy $cw 36 18
    $g.DrawString($c, $chipFont, $mutedBrush, ($cx + 14), ($cy + 8))
    $cy += 48
}
Save-Png $poster (Join-Path $Logos "poster-720x1080.png")
$chipFont.Dispose()
$titleFont.Dispose(); $tagFont.Dispose(); $sf.Dispose()
$g.Dispose(); $poster.Dispose()

# 16:9 super hero
$hero = New-Object System.Drawing.Bitmap 1920, 1080
$g = [System.Drawing.Graphics]::FromImage($hero)
Prep-Graphics $g
$g.Clear($brandBg)
$barBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 27, 36))
$g.FillRectangle($barBrush, 0, 0, 1920, 44)
$tabOn = New-Object System.Drawing.SolidBrush $accent
$tabOff = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, 58, 66, 84))
$px = 18
foreach ($w in @(110, 88, 88, 88)) {
    $br = if ($px -eq 18) { $tabOn } else { $tabOff }
    Fill-RoundRect $g $br $px 14 $w 16 6
    $px += $w + 10
}
$linePen = New-Object System.Drawing.Pen $accent, 2
$g.DrawLine($linePen, 0, 44, 1920, 44)
Draw-IconOn $g 120 280 280
$titleFont = New-Font "Segoe UI" 72 ([System.Drawing.FontStyle]::Bold)
$tagFont = New-Font "Segoe UI" 28 ([System.Drawing.FontStyle]::Regular)
$g.DrawString("No Click Switch", $titleFont, $fgBrush, 460, 330)
$g.DrawString("Always-on-top window switcher for Windows  -  NCS", $tagFont, $mutedBrush, 460, 430)
$chipFont = New-Font "Segoe UI" 18 ([System.Drawing.FontStyle]::Regular)
$cx = 460
foreach ($c in @("Hover to switch", "Tabs per window", "CPU  |  MEM  |  Temps")) {
    $sz = $g.MeasureString($c, $chipFont)
    $cw = [int]$sz.Width + 32
    Fill-RoundRect $g $chipBg $cx 510 $cw 40 20
    $g.DrawString($c, $chipFont, $mutedBrush, ($cx + 16), 518)
    $cx += $cw + 14
}
$footFont = New-Font "Segoe UI" 18 ([System.Drawing.FontStyle]::Regular)
$g.DrawString("github.com/william-bohannan/no-click-switch", $footFont, $mutedBrush, 120, 1000)
$g.DrawString("noclickswitch.com", $footFont, $fgBrush, 1580, 1000)
Save-Png $hero (Join-Path $Promo "super-hero-1920x1080.png")
$barBrush.Dispose(); $tabOn.Dispose(); $tabOff.Dispose(); $linePen.Dispose()
$titleFont.Dispose(); $tagFont.Dispose(); $chipFont.Dispose(); $footFont.Dispose()
$g.Dispose(); $hero.Dispose()

# --- screenshot helpers ---
function New-Desktop([bool]$dark) {
    $bmp = New-Object System.Drawing.Bitmap 1920, 1080
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Prep-Graphics $g
    if ($dark) {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 22, 28, 38))
        $c1 = [System.Drawing.Color]::FromArgb(70, 47, 134, 232)
        $c2 = [System.Drawing.Color]::FromArgb(40, 20, 40, 60)
    } else {
        $g.Clear([System.Drawing.Color]::FromArgb(255, 232, 240, 248))
        $c1 = [System.Drawing.Color]::FromArgb(90, 180, 210, 240)
        $c2 = [System.Drawing.Color]::FromArgb(50, 255, 255, 255)
    }
    $b1 = New-Object System.Drawing.SolidBrush $c1
    $b2 = New-Object System.Drawing.SolidBrush $c2
    $g.FillEllipse($b1, -120, -80, 900, 700)
    $g.FillEllipse($b2, 1100, 400, 1000, 800)
    $b1.Dispose(); $b2.Dispose()
    return @{ Bmp = $bmp; G = $g }
}

function Draw-FakeWindow($g, [int]$x, [int]$y, [int]$w, [int]$h, [string]$title, [bool]$dark, [bool]$front) {
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 0, 0, 0))
    Fill-RoundRect $g $shadow ($x + 8) ($y + 10) $w $h 10
    $shadow.Dispose()
    $chrome = if ($dark) { [System.Drawing.Color]::FromArgb(255, 45, 45, 48) } else { [System.Drawing.Color]::FromArgb(255, 243, 243, 243) }
    $body = if ($dark) { [System.Drawing.Color]::FromArgb(255, 30, 30, 30) } else { [System.Drawing.Color]::FromArgb(255, 252, 252, 252) }
    $br = New-Object System.Drawing.SolidBrush $chrome
    Fill-RoundRect $g $br $x $y $w $h 10
    $br.Dispose()
    $br = New-Object System.Drawing.SolidBrush $body
    Fill-RoundRect $g $br $x ($y + 36) $w ($h - 36) 10
    $g.FillRectangle($br, $x, ($y + 36), $w, 16)
    $br.Dispose()
    $tf = New-Font "Segoe UI" 13 ([System.Drawing.FontStyle]::Regular)
    $tb = New-Object System.Drawing.SolidBrush $(if ($dark) { [System.Drawing.Color]::FromArgb(255, 220, 220, 220) } else { [System.Drawing.Color]::FromArgb(255, 50, 50, 50) })
    $g.DrawString($title, $tf, $tb, ($x + 14), ($y + 8))
    $tf.Dispose(); $tb.Dispose()
    $dotY = $y + 14
    $dx = $x + $w - 18
    foreach ($col in @([System.Drawing.Color]::FromArgb(255, 16, 124, 16), [System.Drawing.Color]::FromArgb(255, 255, 185, 0), [System.Drawing.Color]::FromArgb(255, 232, 17, 35))) {
        $db = New-Object System.Drawing.SolidBrush $col
        $g.FillEllipse($db, ($dx - 36), $dotY, 10, 10)
        $dx -= 16
        $db.Dispose()
    }
    $line = New-Object System.Drawing.SolidBrush $(if ($dark) { [System.Drawing.Color]::FromArgb(255, 50, 50, 55) } else { [System.Drawing.Color]::FromArgb(255, 230, 230, 230) })
    for ($i = 0; $i -lt 8; $i++) {
        $lw = [int](($w - 48) * (0.85 - ($i % 3) * 0.12))
        $g.FillRectangle($line, ($x + 24), ($y + 64 + $i * 28), $lw, 10)
    }
    $line.Dispose()
}

function Draw-Hamburger($g, $brush, [int]$x, [int]$y) {
    $g.FillRectangle($brush, $x, $y, 14, 2)
    $g.FillRectangle($brush, $x, ($y + 5), 14, 2)
    $g.FillRectangle($brush, $x, ($y + 10), 14, 2)
}

function Draw-WinLogo($g, $brush, [int]$x, [int]$y) {
    $g.FillRectangle($brush, $x, $y, 6, 6)
    $g.FillRectangle($brush, ($x + 8), $y, 6, 6)
    $g.FillRectangle($brush, $x, ($y + 8), 6, 6)
    $g.FillRectangle($brush, ($x + 8), ($y + 8), 6, 6)
}

function Draw-Folder($g, $brush, [int]$x, [int]$y) {
    $g.FillRectangle($brush, $x, ($y + 4), 16, 11)
    $g.FillRectangle($brush, $x, $y, 7, 5)
}

function Draw-Term($g, $brush, [int]$x, [int]$y) {
    $pen = New-Object System.Drawing.Pen $brush.Color, 2
    $g.DrawLine($pen, $x, ($y + 3), ($x + 5), ($y + 8))
    $g.DrawLine($pen, $x, ($y + 13), ($x + 5), ($y + 8))
    $g.DrawLine($pen, ($x + 7), ($y + 13), ($x + 14), ($y + 13))
    $pen.Dispose()
}

function Draw-NcsBar($g, [int]$barH, [bool]$dark, [int]$hoverIndex, [int]$activeIndex, $tabs) {
    $bg = if ($dark) { [System.Drawing.Color]::FromArgb(255, 32, 32, 32) } else { [System.Drawing.Color]::FromArgb(255, 243, 243, 243) }
    $tabBg = if ($dark) { [System.Drawing.Color]::FromArgb(255, 58, 58, 58) } else { [System.Drawing.Color]::FromArgb(255, 232, 232, 232) }
    $tabBd = if ($dark) { [System.Drawing.Color]::FromArgb(255, 85, 85, 85) } else { [System.Drawing.Color]::FromArgb(255, 200, 200, 200) }
    $actBg = if ($dark) { [System.Drawing.Color]::FromArgb(255, 74, 74, 74) } else { [System.Drawing.Color]::FromArgb(255, 212, 212, 212) }
    $actBd = if ($dark) { [System.Drawing.Color]::FromArgb(255, 119, 119, 119) } else { [System.Drawing.Color]::FromArgb(255, 142, 142, 142) }
    $hovBg = if ($dark) { [System.Drawing.Color]::FromArgb(255, 55, 78, 110) } else { [System.Drawing.Color]::FromArgb(255, 208, 232, 255) }
    $title = if ($dark) { [System.Drawing.Color]::FromArgb(255, 221, 221, 221) } else { [System.Drawing.Color]::FromArgb(255, 68, 68, 68) }
    $titleA = if ($dark) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(255, 34, 34, 34) }
    $chrome = if ($dark) { [System.Drawing.Color]::FromArgb(255, 176, 176, 176) } else { [System.Drawing.Color]::FromArgb(255, 107, 107, 107) }
    $clockT = if ($dark) { [System.Drawing.Color]::FromArgb(255, 240, 240, 240) } else { [System.Drawing.Color]::FromArgb(255, 26, 26, 26) }
    $clockD = if ($dark) { [System.Drawing.Color]::FromArgb(255, 176, 176, 176) } else { [System.Drawing.Color]::FromArgb(255, 85, 85, 85) }

    $bb = New-Object System.Drawing.SolidBrush $bg
    $g.FillRectangle($bb, 0, 0, 1920, $barH)
    $bb.Dispose()
    $cb = New-Object System.Drawing.SolidBrush $chrome
    Draw-Hamburger $g $cb 14 18
    Draw-WinLogo $g $cb 48 17
    Draw-Folder $g $cb 80 17
    Draw-Term $g $cb 112 16
    $cb.Dispose()

    $tabFont = New-Font "Segoe UI" 12 ([System.Drawing.FontStyle]::Regular)
    $tx = 148
    $i = 0
    foreach ($name in $tabs) {
        $isH = ($i -eq $hoverIndex)
        $isA = ($i -eq $activeIndex)
        $fill = if ($isH) { $hovBg } elseif ($isA) { $actBg } else { $tabBg }
        $bd = if ($isH) { $accent } elseif ($isA) { $actBd } else { $tabBd }
        $fb = New-Object System.Drawing.SolidBrush $fill
        Fill-RoundRect $g $fb $tx 10 96 32 6
        $fb.Dispose()
        $pen = New-Object System.Drawing.Pen $bd, 1
        Draw-RoundRect $g $pen $tx 10 96 32 6
        $pen.Dispose()
        $tb = New-Object System.Drawing.SolidBrush $(if ($isA -or $isH) { $titleA } else { $title })
        $g.DrawString($name, $tabFont, $tb, ($tx + 8), 16)
        $tb.Dispose()
        $tx += 102
        $i++
    }
    $tabFont.Dispose()

    $statFont = New-Font "Segoe UI" 11 ([System.Drawing.FontStyle]::Regular)
    $labFont = New-Font "Segoe UI" 8 ([System.Drawing.FontStyle]::Regular)
    $sb = New-Object System.Drawing.SolidBrush $title
    $lb = New-Object System.Drawing.SolidBrush $chrome
    $rx = 1480
    $g.DrawString("CPU", $labFont, $lb, $rx, 8)
    $g.DrawString("18%", $statFont, $sb, $rx, 22)
    $rx += 48
    $g.DrawString("MEM", $labFont, $lb, $rx, 8)
    $g.DrawString("42%", $statFont, $sb, $rx, 22)
    $rx += 52
    $g.DrawString("C:", $labFont, $lb, $rx, 8)
    $g.DrawString("57%", $statFont, $sb, $rx, 22)
    $rx += 48
    $g.DrawString("CPU", $labFont, $lb, $rx, 8)
    $g.DrawString("54C", $statFont, $sb, $rx, 22)
    $rx += 48
    $g.DrawString("GPU", $labFont, $lb, $rx, 8)
    $g.DrawString("48C", $statFont, $sb, $rx, 22)
    $timeFont = New-Font "Segoe UI" 13 ([System.Drawing.FontStyle]::Bold)
    $dateFont = New-Font "Segoe UI" 10 ([System.Drawing.FontStyle]::Regular)
    $tb = New-Object System.Drawing.SolidBrush $clockT
    $db = New-Object System.Drawing.SolidBrush $clockD
    $g.DrawString("4:21 PM", $timeFont, $tb, 1788, 8)
    $g.DrawString("Mon 8 Sep", $dateFont, $db, 1788, 26)
    $statFont.Dispose(); $labFont.Dispose(); $timeFont.Dispose(); $dateFont.Dispose()
    $sb.Dispose(); $lb.Dispose(); $tb.Dispose(); $db.Dispose()
}

function Draw-Settings($g, [int]$x, [int]$y) {
    $w = 780; $h = 520
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 0, 0, 0))
    Fill-RoundRect $g $shadow ($x + 10) ($y + 12) $w $h 8
    $shadow.Dispose()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 245, 245))
    Fill-RoundRect $g $bg $x $y $w $h 8
    $bg.Dispose()
    $top = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 237, 237, 237))
    Fill-RoundRect $g $top $x $y $w 52 8
    $g.FillRectangle($top, $x, ($y + 20), $w, 32)
    $top.Dispose()
    $nav = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 235, 235, 235))
    $g.FillRectangle($nav, $x, ($y + 52), 200, ($h - 52))
    $nav.Dispose()
    $tf = New-Font "Segoe UI" 16 ([System.Drawing.FontStyle]::Bold)
    $sf = New-Font "Segoe UI" 13 ([System.Drawing.FontStyle]::Regular)
    $hf = New-Font "Segoe UI" 10 ([System.Drawing.FontStyle]::Bold)
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 26, 26, 26))
    $mute = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 102, 102, 102))
    $g.DrawString("Settings  -  No Click Switch", $tf, $ink, ($x + 18), ($y + 14))
    $dx = $x + $w - 18
    foreach ($col in @([System.Drawing.Color]::FromArgb(255, 16, 124, 16), [System.Drawing.Color]::FromArgb(255, 255, 185, 0), [System.Drawing.Color]::FromArgb(255, 232, 17, 35))) {
        $db = New-Object System.Drawing.SolidBrush $col
        $g.FillEllipse($db, ($dx - 36), ($y + 18), 10, 10)
        $dx -= 16
        $db.Dispose()
    }
    $items = @("Mode", "Theme", "Appearance", "Hover", "Stats", "Keyboard", "Addons")
    $iy = $y + 70
    $sel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    Fill-RoundRect $g $sel ($x + 10) ($iy - 4) 180 32 4
    $sel.Dispose()
    foreach ($it in $items) {
        $g.DrawString($it, $sf, $ink, ($x + 22), $iy)
        $iy += 36
    }
    $g.DrawString("Mode", $tf, $ink, ($x + 230), ($y + 70))
    $g.DrawString("Overall density of the bar chrome and spacing.", $sf, $mute, ($x + 230), ($y + 100))
    $card = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $bd = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 208, 208, 208)), 1
    Fill-RoundRect $g $card ($x + 230) ($y + 150) 220 90 6
    Draw-RoundRect $g $bd ($x + 230) ($y + 150) 220 90 6
    Fill-RoundRect $g $card ($x + 470) ($y + 150) 220 90 6
    Draw-RoundRect $g $bd ($x + 470) ($y + 150) 220 90 6
    $acc = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($acc, ($x + 246), ($y + 168), 16, 16)
    $g.DrawString("Compact", $sf, $ink, ($x + 270), ($y + 166))
    $g.DrawString("Standard", $sf, $ink, ($x + 486), ($y + 166))
    $g.DrawString("Default. Tighter chrome.", $hf, $mute, ($x + 246), ($y + 196))
    $tf.Dispose(); $sf.Dispose(); $hf.Dispose()
    $ink.Dispose(); $mute.Dispose(); $card.Dispose(); $bd.Dispose(); $acc.Dispose()
}

$tabs = @("Edge", "Explorer", "Terminal", "Notes", "Photos")

# 01 hover
$d = New-Desktop $false
Draw-FakeWindow $d.G 180 140 720 620 "Notes" $false $false
Draw-FakeWindow $d.G 520 200 1100 760 "Photos" $false $true
Draw-NcsBar $d.G 52 $false 4 1 $tabs
Save-Png $d.Bmp (Join-Path $Shots "01-hover-switch-1920x1080.png")
$d.G.Dispose(); $d.Bmp.Dispose()

# 02 tabs
$d = New-Desktop $false
Draw-FakeWindow $d.G 160 160 980 720 "Explorer" $false $true
Draw-FakeWindow $d.G 980 280 720 560 "Terminal" $false $false
Draw-NcsBar $d.G 52 $false -1 1 $tabs
Save-Png $d.Bmp (Join-Path $Shots "02-tabs-1920x1080.png")
$d.G.Dispose(); $d.Bmp.Dispose()

# 03 settings
$d = New-Desktop $false
Draw-NcsBar $d.G 52 $false -1 1 $tabs
Draw-Settings $d.G 570 200
Save-Png $d.Bmp (Join-Path $Shots "03-settings-1920x1080.png")
$d.G.Dispose(); $d.Bmp.Dispose()

# 04 dark
$d = New-Desktop $true
Draw-FakeWindow $d.G 200 150 900 740 "Terminal" $true $true
Draw-FakeWindow $d.G 980 280 720 560 "Notes" $true $false
Draw-NcsBar $d.G 52 $true 2 2 $tabs
Save-Png $d.Bmp (Join-Path $Shots "04-dark-1920x1080.png")
$d.G.Dispose(); $d.Bmp.Dispose()

$fgBrush.Dispose(); $mutedBrush.Dispose(); $chipBg.Dispose()
$icon.Dispose()
Write-Host "Store listing images written under store\"
