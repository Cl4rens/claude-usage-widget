# Generates the preview screenshot for the widget picker (Assets\WidgetScreenshot.png).
# The icons themselves come from the spark SVG:  dotnet run --project tools\RenderIcons
#   powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot "..\Assets"
$orange = [System.Drawing.Color]::FromArgb(217, 119, 87)
$icon = [System.Drawing.Image]::FromFile((Resolve-Path (Join-Path $dir "WidgetIcon.png")))

$w = 300; $h = 304
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
$g.Clear([System.Drawing.Color]::FromArgb(43, 43, 43))
$white = [System.Drawing.Brushes]::White
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(170, 170, 170))
$track = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 70, 70))
$fill = New-Object System.Drawing.SolidBrush $orange
$fSmall = New-Object System.Drawing.Font "Segoe UI", 11, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$fBig = New-Object System.Drawing.Font "Segoe UI Semibold", 30, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawImage($icon, 18, 18, 22, 22)
$g.DrawString("Claude Usage", $fSmall, $white, 46, 22)
$y = 64
foreach ($row in @(@("5h", 23, "15:10"), @("7d", 46, "Sa 06:00"))) {
    $g.DrawString($row[0], $fSmall, $muted, 18, $y)
    $g.DrawString("$($row[1])%", $fBig, $white, 14, $y + 16)
    $g.DrawString($row[2], $fSmall, $muted, 220, $y + 34)
    $g.FillRectangle($track, 18, $y + 62, 264, 6)
    $g.FillRectangle($fill, 18, $y + 62, [int](264 * $row[1] / 100), 6)
    $y += 100
}
$icon.Dispose()
$bmp.Save((Join-Path $dir "WidgetScreenshot.png"), [System.Drawing.Imaging.ImageFormat]::Png)
"Screenshot written to $dir"
