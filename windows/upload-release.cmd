@echo off
rem Выкладывает уже собранный релиз из Releases на GitHub Releases.
rem Использование: upload-release.cmd 1.0.1
rem Токен берётся у gh, в файле его нет.
rem
rem Also uploads SHA256SUMS.txt of the Velopack assets so operators can verify
rem downloads manually. This is NOT Authenticode ? see SECURITY.md ?3.9.

setlocal
set VER=%1
if "%VER%"=="" (echo Укажите версию, например: upload-release.cmd 1.0.1 & exit /b 1)
set ROOT=D:\MyProjects\IPasswrd
set VPK=%USERPROFILE%\.dotnet\tools\vpk.exe
set SUMS=%ROOT%\Releases\SHA256SUMS.txt

for /f "delims=" %%t in ('gh auth token') do set TOK=%%t
if "%TOK%"=="" (echo Нет токена GitHub: выполните gh auth login & exit /b 1)

cd /d "%ROOT%"
"%VPK%" upload github -o Releases --repoUrl https://github.com/yoyoloxxx/ipasswrd --token %TOK% --publish true --releaseName "IPasswrd %VER%" --tag v%VER% > "%ROOT%\vpk-upload.log" 2>&1
if errorlevel 1 (echo === FAILED === >> "%ROOT%\vpk-upload.log") else (echo === OK === >> "%ROOT%\vpk-upload.log")

rem Checksums of release packages (portable / setup / nupkg) for manual verification.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%ROOT%\Releases' -File | Where-Object { $_.Name -match '\.(nupkg|exe|zip)$' } | ForEach-Object { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name } | Set-Content -LiteralPath '%SUMS%' -Encoding ascii" >> "%ROOT%\vpk-upload.log" 2>&1
if exist "%SUMS%" (
  gh release upload v%VER% "%SUMS%" --clobber -R yoyoloxxx/ipasswrd >> "%ROOT%\vpk-upload.log" 2>&1
  type "%SUMS%" >> "%ROOT%\vpk-upload.log"
)

rem Расширение файлом (временно, пока Chrome Web Store проверяет публикацию).
powershell -NoProfile -Command "Compress-Archive -Path '%ROOT%\windows\extension\*' -DestinationPath '%ROOT%\Releases\IPasswrd-extension.zip' -Force" >> "%ROOT%\vpk-upload.log" 2>&1
gh release upload v%VER% "%ROOT%\Releases\IPasswrd-extension.zip" --clobber -R yoyoloxxx/ipasswrd >> "%ROOT%\vpk-upload.log" 2>&1
