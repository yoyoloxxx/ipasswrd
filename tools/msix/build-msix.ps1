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
Add-Type -AssemblyName System.Drawing
$master = [System.Drawing.Image]::FromFile((Join-Path $root 'src\IPasswrd.App\Assets\ipasswrd_app_512.png'))
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function Save-Tile([int]$w, [int]$h, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    # вписываем квадратный мастер по центру, с полями как требует Store
    $side = [Math]::Min($w, $h) * 0.75
    $g.DrawImage($script:master, ($w - $side) / 2, ($h - $side) / 2, $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Save-Tile 44  44  'Square44x44Logo.png'
Save-Tile 150 150 'Square150x150Logo.png'
Save-Tile 310 150 'Wide310x150Logo.png'
Save-Tile 50  50  'StoreLogo.png'
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
