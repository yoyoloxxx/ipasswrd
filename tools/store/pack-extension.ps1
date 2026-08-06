# Готовит расширение к загрузке в Chrome Web Store.
#   .\tools\store\pack-extension.ps1 -Version 1.0.0
#
# Исходник один - папка extension\. Здесь из неё убирается то, что нужно
# только при разработке, и складывается zip.
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'

$root  = 'D:\MyProjects\IPasswrd'
$src   = Join-Path $root 'extension'
$stage = Join-Path $root 'build-extension'
$out   = Join-Path $root 'Store'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $src $stage -Recurse

$m = Get-Content (Join-Path $stage 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# "key" держит постоянный ID у распакованной копии. В Store пакет подписывает Google
# своим ключом и выдаёт СВОЙ ID - чужой ключ там лишний и только путает.
$m.PSObject.Properties.Remove('key')

# Ctrl+Shift+9 перезагружает расширение - вещь сугубо отладочная, да и
# chrome.runtime.reload в опубликованном расширении вызывает лишние вопросы.
$m.PSObject.Properties.Remove('commands')

$m.version = $Version

# ConvertTo-Json экранирует кириллицу в \uXXXX - для манифеста это допустимо и
# безопаснее, чем ловить BOM и кодировки.
$json = $m | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $json, (New-Object System.Text.UTF8Encoding $false))

New-Item -ItemType Directory -Force -Path $out | Out-Null
$zip = Join-Path $out "ipasswrd-extension-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Remove-Item $stage -Recurse -Force
"OK: $zip (" + [math]::Round((Get-Item $zip).Length/1KB) + " KB)"
"Загружать сюда: https://chrome.google.com/webstore/devconsole"
