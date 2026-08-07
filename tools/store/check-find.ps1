# Разовая проверка команды find на подставном сейфе. Настоящий сейф не трогается:
# IPASSWRD_VAULT живёт только внутри этого процесса.
$ErrorActionPreference = 'Stop'
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

$root = 'D:\MyProjects\IPasswrd'
$cli  = Join-Path $root 'src\IPasswrd.Cli\bin\Release\net10.0\ipasswrd.exe'
$pw   = 'test-vault-2026'

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\store\make-test-vault.ps1') | Out-Null
$env:IPASSWRD_VAULT = Join-Path $root 'test-vault\vault.ipvault'

function Try-Find($q) {
    $out = ("$pw" | & $cli find $q) 2>&1 | Out-String
    return $out
}

$cases = @(
    @{ q = 'невский';        want = 'Петров';         note = 'адрес по улице' },
    @{ q = 'petrov';         want = 'Тестовая карта'; note = 'карта по держателю' },
    @{ q = 'карта petrov';   want = 'Тестовая карта'; note = 'слова из двух полей' },
    @{ q = 'petrov карта';   want = 'Тестовая карта'; note = 'порядок слов не важен' },
    @{ q = '190000';         want = 'Петров';         note = 'индекс' }
)

foreach ($c in $cases) {
    $out = Try-Find $c.q
    $ok = $out -match [regex]::Escape($c.want)
    "{0,-22} {1,-24} {2}" -f $c.q, $c.note, $(if ($ok) { 'НАШЁЛ' } else { "НЕ НАШЁЛ <<<`n$out" })
}

"--- не должно находиться ---"
$out = Try-Find '123'
"find 123 (CVC карты = 123, телефон содержит 123)"
$out.Trim()
if ($out -match 'Тестовая карта') { 'ПРОВАЛ: карта нашлась по CVC <<<' } else { 'OK: карта по CVC не находится' }

$out = Try-Find '4111'
if ($out -match 'Тестовая карта') { 'OK: карта находится по номеру' } else { 'ПРОВАЛ: номер карты не ищется <<<' }

Remove-Item Env:IPASSWRD_VAULT
Remove-Item (Join-Path $root 'test-vault') -Recurse -Force
'подставной сейф удалён'
