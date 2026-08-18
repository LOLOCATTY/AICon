# Generates the AICon ribbon icons (PNG) using System.Drawing.
# Output: assets\icons\aicon{16,32,256}.png, log{16,32}.png, about{16,32}.png
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "icons"
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

function RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Save-Scaled([System.Drawing.Bitmap]$src, [int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $size, $size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# ---------- AICon logo ----------
$S = 256
$bmp, $g = New-Canvas $S

# Background: rounded square, blue -> violet diagonal gradient
$rect = RoundedRect 8 8 240 240 56
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point($S, $S)),
    [System.Drawing.Color]::FromArgb(255, 37, 99, 235),   # blue #2563EB
    [System.Drawing.Color]::FromArgb(255, 139, 92, 246))  # violet #8B5CF6
$g.FillPath($grad, $rect)

# Subtle top highlight
$hl = RoundedRect 8 8 240 120 56
$hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 8)), (New-Object System.Drawing.Point(0, 128)),
    [System.Drawing.Color]::FromArgb(70, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$g.FillPath($hlBrush, $hl)

# "AI" text
$font = New-Object System.Drawing.Font("Segoe UI", 100, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$white = [System.Drawing.Brushes]::White
$g.DrawString("AI", $font, $white, (New-Object System.Drawing.RectangleF(0, 6, $S, 170)), $fmt)

# Connection motif: three nodes linked by lines (the "Con")
$penLine = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 255, 255, 255), 7)
$nodes = @(@(70, 196), @(128, 176), @(186, 196))
$g.DrawLine($penLine, $nodes[0][0], $nodes[0][1], $nodes[1][0], $nodes[1][1])
$g.DrawLine($penLine, $nodes[1][0], $nodes[1][1], $nodes[2][0], $nodes[2][1])
foreach ($n in $nodes) {
    $g.FillEllipse($white, $n[0] - 11, $n[1] - 11, 22, 22)
}

$bmp.Save((Join-Path $outDir "aicon256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
Save-Scaled $bmp 32 (Join-Path $outDir "aicon32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "aicon16.png")
$g.Dispose(); $bmp.Dispose()

# ---------- Log icon (page with lines) ----------
$bmp, $g = New-Canvas 256
$page = RoundedRect 48 24 160 208 20
$g.FillPath(([System.Drawing.Brushes]::White), $page)
$penEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 71, 85, 105), 12)
$g.DrawPath($penEdge, $page)
$penText = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 37, 99, 235), 14)
foreach ($y in 76, 116, 156) { $g.DrawLine($penText, 80, $y, 176, $y) }
$penShort = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 139, 92, 246), 14)
$g.DrawLine($penShort, 80, 196, 140, 196)
Save-Scaled $bmp 32 (Join-Path $outDir "log32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "log16.png")
$g.Dispose(); $bmp.Dispose()

# ---------- About icon (info circle) ----------
$bmp, $g = New-Canvas 256
$circleBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(256, 256)),
    [System.Drawing.Color]::FromArgb(255, 37, 99, 235),
    [System.Drawing.Color]::FromArgb(255, 139, 92, 246))
$g.FillEllipse($circleBrush, 24, 24, 208, 208)
$fontI = New-Object System.Drawing.Font("Georgia", 140, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString("i", $fontI, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 22, 256, 212)), $fmt)
Save-Scaled $bmp 32 (Join-Path $outDir "about32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "about16.png")
$g.Dispose(); $bmp.Dispose()

# ---------- Agent buttons (Gemini / DeepSeek / Local) ----------
# Each is a rounded-square tile in the agent's colour with a simple white glyph, scaled to 32 & 16.

function New-Tile([int]$size, [System.Drawing.Color]$c1, [System.Drawing.Color]$c2) {
    $bmp, $g = New-Canvas $size
    $tile = RoundedRect 8 8 ($size - 16) ($size - 16) 48
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point($size, $size)), $c1, $c2)
    $g.FillPath($grad, $tile)
    # soft top highlight
    $hl = RoundedRect 8 8 ($size - 16) (($size - 16) / 2) 48
    $hlBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 8)), (New-Object System.Drawing.Point(0, ($size / 2))),
        [System.Drawing.Color]::FromArgb(60, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hlBrush, $hl)
    return @($bmp, $g)
}

function StarPath([single]$cx, [single]$cy, [single]$outer, [single]$inner, [int]$points) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $step = [Math]::PI / $points
    for ($i = 0; $i -lt ($points * 2); $i++) {
        if ($i % 2 -eq 0) { $r = $outer } else { $r = $inner }
        $a = $i * $step - [Math]::PI / 2
        $pts.Add((New-Object System.Drawing.PointF([single]($cx + $r * [Math]::Cos($a)), [single]($cy + $r * [Math]::Sin($a)))))
    }
    $path.AddPolygon($pts.ToArray())
    return $path
}

$white = [System.Drawing.Brushes]::White
$S = 256

# Gemini: blue -> violet tile, white 4-point sparkle
$bmp, $g = New-Tile $S ([System.Drawing.Color]::FromArgb(255, 26, 115, 232)) ([System.Drawing.Color]::FromArgb(255, 161, 66, 244))
$star = StarPath 128 128 92 22 4
$g.FillPath($white, $star)
Save-Scaled $bmp 32 (Join-Path $outDir "gemini32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "gemini16.png")
$g.Dispose(); $bmp.Dispose()

# DeepSeek: deep-blue tile, white concentric "sonar" rings (seeking depth)
$bmp, $g = New-Tile $S ([System.Drawing.Color]::FromArgb(255, 77, 107, 254)) ([System.Drawing.Color]::FromArgb(255, 30, 64, 224))
$penRing = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 15)
$penRing.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penRing.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
foreach ($rad in 34, 64, 94) { $g.DrawArc($penRing, (128 - $rad), (150 - $rad), ($rad * 2), ($rad * 2), 200, 140) }
$g.FillEllipse($white, 116, 138, 24, 24)
Save-Scaled $bmp 32 (Join-Path $outDir "deepseek32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "deepseek16.png")
$g.Dispose(); $bmp.Dispose()

# Local: emerald -> teal tile, white CPU chip (runs on your own machine)
$bmp, $g = New-Tile $S ([System.Drawing.Color]::FromArgb(255, 16, 185, 129)) ([System.Drawing.Color]::FromArgb(255, 14, 165, 165))
$penChip = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 14)
$chip = RoundedRect 86 86 84 84 12
$g.DrawPath($penChip, $chip)
$g.FillRectangle($white, 112, 112, 32, 32)
$penPin = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 12)
$penPin.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$penPin.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
foreach ($x in 104, 128, 152) {
    $g.DrawLine($penPin, $x, 66, $x, 86)     # top pins
    $g.DrawLine($penPin, $x, 170, $x, 190)   # bottom pins
}
foreach ($y in 104, 128, 152) {
    $g.DrawLine($penPin, 66, $y, 86, $y)     # left pins
    $g.DrawLine($penPin, 170, $y, 190, $y)   # right pins
}
Save-Scaled $bmp 32 (Join-Path $outDir "local32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "local16.png")
$g.Dispose(); $bmp.Dispose()

# ---------- AR400 icon (floor plan blueprint: outline + a partition line) ----------
$bmp, $g = New-Canvas 256
$tile = RoundedRect 8 8 240 240 48
$gradAr = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(256, 256)),
    [System.Drawing.Color]::FromArgb(255, 30, 41, 59),    # slate-800
    [System.Drawing.Color]::FromArgb(255, 71, 85, 105))   # slate-600
$g.FillPath($gradAr, $tile)
$penPlan = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 12)
$penPlan.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$g.DrawRectangle($penPlan, 56, 56, 144, 144)
$penWall = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 9)
$g.DrawLine($penWall, 128, 56, 128, 130)   # internal partition
$g.DrawLine($penWall, 128, 150, 128, 200)
$penDoor = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 96, 165, 250), 7)
$g.DrawArc($penDoor, 98, 118, 60, 60, 180, 90)   # door swing arc
Save-Scaled $bmp 32 (Join-Path $outDir "ar40032.png")
Save-Scaled $bmp 16 (Join-Path $outDir "ar40016.png")
$g.Dispose(); $bmp.Dispose()

# ---------- Routine icon (a stored "play" action: rounded tile + play triangle on a card) ----------
$bmp, $g = New-Canvas 256
$tile = RoundedRect 8 8 240 240 48
$gradR = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(256, 256)),
    [System.Drawing.Color]::FromArgb(255, 217, 119, 6),    # amber-600
    [System.Drawing.Color]::FromArgb(255, 180, 83, 9))     # amber-700
$g.FillPath($gradR, $tile)
# card behind
$card = RoundedRect 62 56 132 144 16
$g.FillPath([System.Drawing.Brushes]::White, $card)
# play triangle
$tri = New-Object System.Drawing.Drawing2D.GraphicsPath
$tri.AddPolygon(@(
    (New-Object System.Drawing.PointF(112, 96)),
    (New-Object System.Drawing.PointF(112, 160)),
    (New-Object System.Drawing.PointF(164, 128))))
$g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 180, 83, 9))), $tri)
Save-Scaled $bmp 32 (Join-Path $outDir "routine32.png")
Save-Scaled $bmp 16 (Join-Path $outDir "routine16.png")
$g.Dispose(); $bmp.Dispose()

Write-Host "Icons written to $outDir"
Get-ChildItem $outDir | Select-Object Name, Length
