# Собирает ПОДСТАВНОЙ сейф для скриншотов витрины магазинов.
# Настоящий сейф не трогается вообще: переменная IPASSWRD_VAULT уводит и сейф,
# и настройки приложения в отдельную папку, так что и синхронизация туда не лезет.
$ErrorActionPreference = 'Stop'

$root = 'D:\MyProjects\IPasswrd'
$demo = Join-Path $root 'demo-vault'
$cli  = Join-Path $root 'src\IPasswrd.Cli\bin\Release\net10.0\ipasswrd.exe'

if (Test-Path $demo) { Remove-Item $demo -Recurse -Force }
New-Item -ItemType Directory -Force -Path $demo | Out-Null

if (-not (Test-Path $cli)) {
    & 'C:\Program Files\dotnet\dotnet.exe' build (Join-Path $root 'src\IPasswrd.Cli\IPasswrd.Cli.csproj') -c Release --nologo | Out-Null
}

$env:IPASSWRD_VAULT = Join-Path $demo 'vault.ipvault'
$pw = 'demo-vault-2026'

"demo1: init"
"$pw`n$pw" | & $cli init

# Пароли нарочно разной силы: на скриншоте проверки безопасности должно быть
# что показывать, иначе экран выглядит пустым и бессмысленным.
$csv = Join-Path $demo 'demo.csv'
@'
name,url,username,password
Госуслуги,https://gosuslugi.ru,ivan.petrov@mail.ru,7Kq!vR2m#Xz9Ld4w
Ozon,https://ozon.ru,ivan.petrov@mail.ru,Pm3$wQx8!Ryt6Nfa
Т-Банк,https://tbank.ru,+7 900 123-45-67,Vt9#Lz2Qm!Xk7Bre
Яндекс,https://yandex.ru,ivan.petrov,Zq4!Nm8wRx#Ty2Lp
Wildberries,https://wildberries.ru,+7 900 123-45-67,qwerty123
Авито,https://avito.ru,ivan.petrov@mail.ru,Hn6#Wq3Lz!Rm9Xkt
VK,https://vk.com,ivan.petrov,ivan2010
Steam,https://steampowered.com,ipetrov_gaming,Bk8!Tz5Nq#Wm2Rvx
GitHub,https://github.com,ivanpetrov,Lw7#Qm4Zx!Nt9Bkr
Кинопоиск,https://kinopoisk.ru,ivan.petrov@mail.ru,ivan2010
Почта Mail.ru,https://mail.ru,ivan.petrov@mail.ru,Rt2!Xk9Wm#Qz5Lnb
Мегамаркет,https://megamarket.ru,+7 900 123-45-67,Nq5#Bt8Lx!Zw3Rkm
'@ | Out-File -FilePath $csv -Encoding UTF8

"demo2: import"
"$pw" | & $cli import $csv

"demo3: list"
"$pw" | & $cli list

"OK. Запускать приложение так:"
"  `$env:IPASSWRD_VAULT = '$env:IPASSWRD_VAULT'; мастер-пароль: $pw"
