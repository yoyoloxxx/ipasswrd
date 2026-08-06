# ЛОКАЛЬНАЯ ПРОВЕРКА пакета MSIX. В Store так делать не нужно - там Microsoft
# подписывает сам; это только чтобы установить пакет у себя и убедиться, что
# приложение в песочнице не потеряло сейф.
#
# ТРЕБУЕТ ПРАВ АДМИНИСТРАТОРА: самоподписанный сертификат кладётся в
# доверенные (Cert:\LocalMachine\TrustedPeople), иначе Windows пакет не примет.
# Сертификат одноразовый, живёт год, подписывать им можно только пакеты с этим
# же издателем. Убрать: см. последнюю строку файла.
$ErrorActionPreference = 'Stop'

$publisher = 'CN=847C0328-6A65-405B-AF69-647AF7B6E509'   # ровно как в манифесте
$root = 'D:\MyProjects\IPasswrd'
$sdk  = Join-Path $root 'tools\msix\sdk'
$msix = (Get-ChildItem (Join-Path $root 'Store\*.msix') | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
"package: $msix"

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
        -KeyUsage DigitalSignature -FriendlyName 'IPasswrd local MSIX test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    "created cert: " + $cert.Thumbprint
} else { "reusing cert: " + $cert.Thumbprint }

$pfx = Join-Path $env:TEMP 'ipasswrd-msix-test.pfx'
$pw  = ConvertTo-SecureString -String 'ipw-local-test' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null

# в доверенные - только этот сертификат, только для установки пакетов
Import-PfxCertificate -FilePath $pfx -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $pw | Out-Null

& (Join-Path $sdk 'signtool.exe') sign /fd SHA256 /a /f $pfx /p 'ipw-local-test' $msix
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

Get-Process IPasswrd.App -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion
Remove-Item $pfx -Force

Get-AppxPackage -Name 'yoyoloxxx.IPasswrd' | Select-Object Name, Version, InstallLocation | Format-List

# УБРАТЬ ПОСЛЕ ПРОВЕРКИ:
#   Remove-AppxPackage (Get-AppxPackage -Name 'yoyoloxxx.IPasswrd').PackageFullName
#   Get-ChildItem Cert:\LocalMachine\TrustedPeople | ? { $_.Subject -eq 'CN=847C0328-6A65-405B-AF69-647AF7B6E509' } | Remove-Item
