@echo off
rem Launcher for the IPasswrd desktop app. Double-click, or use the IPasswrd.lnk shortcut.
cd /d "%~dp0"
set "EXE=IPasswrd.App\bin\Debug\net10.0\IPasswrd.App.exe"
if not exist "%EXE%" (
  echo Building IPasswrd, please wait...
  dotnet build "IPasswrd.App\IPasswrd.App.csproj" -v minimal || goto :error
)
start "" "%EXE%"
exit /b 0

:error
echo.
echo Build failed. Make sure the .NET 10 SDK is installed (dotnet --version).
pause
exit /b 1
