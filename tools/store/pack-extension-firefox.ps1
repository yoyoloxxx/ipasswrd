# Готовит расширение к загрузке на addons.mozilla.org.
#   .\tools\store\pack-extension-firefox.ps1 -Version 1.0.1
#
# Исходник тот же — папка extension\. Отличается только манифест: Firefox в MV3
# не умеет service_worker и опознаёт расширение по id вида name@domain.
#
# ВНИМАНИЕ: собранный пакет ни разу не запускался в Firefox — браузера не было
# под рукой. Перед отправкой на проверку его нужно поставить как временное
# дополнение (about:debugging → «Загрузить временное дополнение») и убедиться,
# что подстановка логина, карты и личных данных работает.
param([string]$Version = '1.0.1')
$ErrorActionPreference = 'Stop'

$root  = 'D:\MyProjects\IPasswrd'
$src   = Join-Path $root 'extension'
$stage = Join-Path $root 'build-extension-ff'
$out   = Join-Path $root 'Store'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $src $stage -Recurse

# Манифест Chrome в пакете Firefox лишний и только сбивает проверяющих.
Remove-Item (Join-Path $stage 'manifest.json') -Force
Move-Item (Join-Path $stage 'manifest.firefox.json') (Join-Path $stage 'manifest.json') -Force

$m = Get-Content (Join-Path $stage 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$m.version = $Version
# Пояснения нужны читателю исходника, а не проверяющему пакет.
foreach ($p in @($m.PSObject.Properties.Name | Where-Object { $_ -like '_comment*' })) {
    $m.PSObject.Properties.Remove($p)
}
$json = $m | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), $json, (New-Object System.Text.UTF8Encoding $false))

New-Item -ItemType Directory -Force -Path $out | Out-Null
$zip = Join-Path $out "ipasswrd-firefox-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Remove-Item $stage -Recurse -Force
"OK: $zip (" + [math]::Round((Get-Item $zip).Length/1KB) + " KB)"
"Загружать сюда: https://addons.mozilla.org/developers/"
"Перед отправкой — проверить вживую: about:debugging → «Загрузить временное дополнение»"
