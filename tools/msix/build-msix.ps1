# Собирает пакет MSIX для Microsoft Store.
#   .\tools\msix\build-msix.ps1 -Version 1.0.5
#
# Velopack в этот пакет НЕ кладётся: обновления в Store делает сам Store.
param([string]$Version = '1.0.5')
$ErrorActionPreference = 'Stop'

$root  = 'D:\MyProjects\IPasswrd'
$stage = Join-Path $root 'build-msix'
$out   = Join-Path $root 'Store'
$sdk   = Join-Path $root 'tools\msix\sdk'
$src   = Join-Path $root 'tools\msix'

# --- 1. публикация ---
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
& 'C:\Program Files\dotnet\dotnet.exe' publish (Join-Path $root 'src\IPasswrd.App\IPasswrd.App.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None `
    -p:Version=$Version -o $stage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Расширение и нативный хост едут внутри пакета, как и в обычной сборке.
Copy-Item (Join-Path $root 'extension') (Join-Path $stage 'extension') -Recurse -Force
$hostExe = Join-Path $root 'dist-host\IPasswrd.Host.exe'
if (Test-Path $hostExe) { Copy-Item $hostExe $stage -Force }

# --- 2. плитки ---
# Store проверяет размеры, поэтому режем из мастера 512x512, а не переименовываем.
#
# ПЛИТКИ — С ФИРМЕННЫМ ТЁМНЫМ ФОНОМ, НЕ ПРОЗРАЧНЫЕ. Прозрачные места плитки Windows
# заливает акцентным цветом системы — у кого-то он фиолетовый, и логотип плавает на
# случайной подложке. Цвет фона = IpBg тёмной темы приложения (#090C10) — плитка
# выглядит как маленький экран входа, на любом акценте одинаковая.
# Для панели задач отдельно кладутся altform-unplated — там подложки нет вовсе,
# один логотип, — именно их Windows берёт для таскбара и списка Пуска.
Add-Type -AssemblyName System.Drawing
$master = [System.Drawing.Image]::FromFile((Join-Path $root 'src\IPasswrd.App\Assets\ipasswrd_app_512.png'))
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$brandBg = [System.Drawing.Color]::FromArgb(255, 0x09, 0x0C, 0x10)   # IpBg тёмной темы

function Save-Tile([int]$w, [int]$h, [string]$name, [double]$scale = 0.75, [bool]$plated = $true) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    if ($plated) { $g.Clear($script:brandBg) } else { $g.Clear([System.Drawing.Color]::Transparent) }
    # вписываем квадратный мастер по центру
    $side = [Math]::Min($w, $h) * $scale
    $g.DrawImage($script:master, ($w - $side) / 2, ($h - $side) / 2, $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Save-Tile 44  44  'Square44x44Logo.png'
Save-Tile 150 150 'Square150x150Logo.png'
Save-Tile 310 150 'Wide310x150Logo.png'
Save-Tile 50  50  'StoreLogo.png'
# Без подложки — для таскбара и списка Пуска (логотип крупнее: полей плитки тут не нужно).
foreach ($ts in 16, 24, 32, 48, 256) {
    Save-Tile $ts $ts ("Square44x44Logo.targetsize-$ts.png") 0.92 $false
    Save-Tile $ts $ts ("Square44x44Logo.targetsize-${ts}_altform-unplated.png") 0.92 $false
}
$master.Dispose()

# --- 3. манифест с подставленной версией ---
# Четыре части, последняя обязана быть 0 - Store отклоняет пакеты с ненулевой ревизией.
$manifest = Get-Content (Join-Path $src 'AppxManifest.xml') -Raw -Encoding UTF8
$manifest = $manifest -replace 'Version="0\.0\.0\.0"', "Version=`"$Version.0`""
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

# --- 4. упаковка ---
New-Item -ItemType Directory -Force -Path $out | Out-Null
$msix = Join-Path $out "IPasswrd-$Version.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& (Join-Path $sdk 'makeappx.exe') pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

"OK: $msix (" + [math]::Round((Get-Item $msix).Length/1MB,1) + " MB)"
