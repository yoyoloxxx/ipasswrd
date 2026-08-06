@echo off
rem Выкладывает уже собранный релиз из Releases на GitHub Releases.
rem Использование: upload-release.cmd 1.0.1
rem Токен берётся у gh, в файле его нет.

setlocal
set VER=%1
if "%VER%"=="" (echo Укажите версию, например: upload-release.cmd 1.0.1 & exit /b 1)
set ROOT=D:\MyProjects\IPasswrd
set VPK=%USERPROFILE%\.dotnet\tools\vpk.exe

for /f "delims=" %%t in ('gh auth token') do set TOK=%%t
if "%TOK%"=="" (echo Нет токена GitHub: выполните gh auth login & exit /b 1)

cd /d "%ROOT%"
"%VPK%" upload github -o Releases --repoUrl https://github.com/yoyololka/ipasswrd --token %TOK% --publish true --releaseName "IPasswrd %VER%" --tag v%VER% > "%ROOT%\vpk-upload.log" 2>&1
if errorlevel 1 (echo === FAILED === >> "%ROOT%\vpk-upload.log") else (echo === OK === >> "%ROOT%\vpk-upload.log")
