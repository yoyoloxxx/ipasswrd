# ????????? ?????? ??? ??????? Chrome Web Store.
# ? ??????? ??????? 24-?????? PNG ??? ?????-??????, ??????? ??????? ????????
# ?? ???????? ???, ? ?? ??????????? ? ?????????????.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = 'D:\MyProjects\IPasswrd'
$out  = Join-Path $root 'Store\screenshots'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$logo = [System.Drawing.Image]::FromFile((Join-Path $root 'windows\IPasswrd.App\Assets\ipasswrd_app_512.png'))

# ????????? ????? ?? App.axaml (?????? ????)
$bg     = [System.Drawing.Color]::FromArgb(20, 18, 14)
$accent = [System.Drawing.Color]::FromArgb(225, 184, 94)
$text   = [System.Drawing.Color]::FromArgb(236, 231, 220)
$muted  = [System.Drawing.Color]::FromArgb(164, 156, 139)

function New-Promo([int]$w, [int]$h, [string]$name, [int]$logoSize, [single]$titleSize, [single]$subSize) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear($bg)

    # ?????? ????? ?? ????????? - ????? ?????? ???????? ??????? ????????
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(-($w * 0.1), -($h * 0.35), $w * 0.75, $h * 1.1)
    $br = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $br.CenterColor = [System.Drawing.Color]::FromArgb(38, 225, 184, 94)
    $br.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 225, 184, 94))
    $g.FillPath($br, $glow)

    $pad = [int]($w * 0.09)
    $ly = [int](($h - $logoSize) / 2)
    $g.DrawImage($logo, $pad, $ly, $logoSize, $logoSize)

    $tx = $pad + $logoSize + [int]($w * 0.05)
    $fT = New-Object System.Drawing.Font('Segoe UI', $titleSize, [System.Drawing.FontStyle]::Bold)
    $fS = New-Object System.Drawing.Font('Segoe UI', $subSize, [System.Drawing.FontStyle]::Regular)
    $bT = New-Object System.Drawing.SolidBrush $text
    $bS = New-Object System.Drawing.SolidBrush $muted
    $bA = New-Object System.Drawing.SolidBrush $accent

    $th = $g.MeasureString('IPasswrd', $fT).Height
    $sh = $g.MeasureString('x', $fS).Height
    $ty = [int](($h - ($th + $sh * 2.4)) / 2)

    $g.DrawString('IPasswrd', $fT, $bT, $tx, $ty)
    $g.DrawString('???????? ???????', $fS, $bS, $tx, $ty + $th + $sh * 0.15)
    $g.DrawString('???? ???????? ? ???', $fS, $bA, $tx, $ty + $th + $sh * 1.25)

    $g.Dispose()
    $p = Join-Path $out $name
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "$p  ($w x $h)"
}

New-Promo 440 280 'promo-small-440x280.png' 128 20 9.5
New-Promo 1400 560 'promo-large-1400x560.png' 300 52 24
$logo.Dispose()
