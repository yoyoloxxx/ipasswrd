# Подставной сейф для ПРОВЕРКИ автозаполнения. Настоящий сейф не трогается:
# IPASSWRD_VAULT уводит и сейф, и настройки приложения в отдельную папку.
#
# Отличие от make-demo-vault.ps1: тут не витринные скриншоты, а данные, на которых
# видно, правильно ли расширение разложило поля по форме доставки.
$ErrorActionPreference = 'Stop'

# PowerShell по умолчанию шлёт в конвейер ASCII, и кириллица превращается в вопросительные знаки
# ещё до того, как её увидит программа.
$OutputEncoding = New-Object System.Text.UTF8Encoding $false

$root = 'D:\MyProjects\IPasswrd'
$test = Join-Path $root 'test-vault'
$cli  = Join-Path $root 'src\IPasswrd.Cli\bin\Release\net10.0\ipasswrd.exe'

if (Test-Path $test) { Remove-Item $test -Recurse -Force }
New-Item -ItemType Directory -Force -Path $test | Out-Null

$env:IPASSWRD_VAULT = Join-Path $test 'vault.ipvault'
$pw = 'test-vault-2026'

"1: init"
"$pw`n$pw" | & $cli init

"2: identity"
# Пусто в первой строке — название соберётся из ФИО, как и в приложении.
@"
$pw

Петров
Иван
Сергеевич
+7 900 123-45-67
ivan.petrov@mail.ru
190000
Россия
Санкт-Петербург
Невский проспект, 28, кв. 15
"@ | & $cli add identity

"3: card"
@"
$pw
Тестовая карта
4111111111111111
12/29
123
IVAN PETROV

"@ | & $cli add card

"4: list"
"$pw" | & $cli list

"OK: `$env:IPASSWRD_VAULT = '$env:IPASSWRD_VAULT'; мастер-пароль: $pw"
