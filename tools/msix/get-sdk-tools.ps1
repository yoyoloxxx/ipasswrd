# Достаёт makeappx.exe (и signtool.exe) без установки всего Windows SDK:
# пакет Microsoft.Windows.SDK.BuildTools с nuget - это обычный zip на ~50 МБ.
$ErrorActionPreference = 'Stop'
$root = 'D:\MyProjects\IPasswrd\tools\msix'
$dest = Join-Path $root 'sdk'

if (Test-Path (Join-Path $dest 'makeappx.exe')) { "already have makeappx"; exit 0 }

$idx = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json' -TimeoutSec 60
$ver = $idx.versions | Where-Object { $_ -notmatch 'preview' } | Select-Object -Last 1
"version: $ver"

$nupkg = Join-Path $root "sdk-buildtools.$ver.zip"
Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ver/microsoft.windows.sdk.buildtools.$ver.nupkg" -OutFile $nupkg -TimeoutSec 600
"downloaded: " + [math]::Round((Get-Item $nupkg).Length/1MB,1) + " MB"

$tmp = Join-Path $root '_unzip'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive $nupkg -DestinationPath $tmp -Force

$src = Get-ChildItem (Join-Path $tmp 'bin') -Directory | Select-Object -Last 1
$x64 = Join-Path $src.FullName 'x64'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $x64 '*') $dest -Recurse -Force

Remove-Item $tmp -Recurse -Force
Remove-Item $nupkg -Force
"makeappx: " + (Test-Path (Join-Path $dest 'makeappx.exe'))
"signtool: " + (Test-Path (Join-Path $dest 'signtool.exe'))
