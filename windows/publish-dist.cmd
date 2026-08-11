@echo off
"C:\Program Files\dotnet\dotnet.exe" publish "D:\MyProjects\IPasswrd\windows\IPasswrd.App\IPasswrd.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "D:\MyProjects\IPasswrd\dist" > "D:\MyProjects\IPasswrd\dist-publish.log" 2>&1

rem Bundle the browser extension and native host next to the app so "Install extension"
rem finds everything in one folder (portable install, e.g. for another PC).
xcopy /E /I /Y "D:\MyProjects\IPasswrd\windows\extension" "D:\MyProjects\IPasswrd\dist\extension" >> "D:\MyProjects\IPasswrd\dist-publish.log" 2>&1
if exist "D:\MyProjects\IPasswrd\dist-host\IPasswrd.Host.exe" copy /Y "D:\MyProjects\IPasswrd\dist-host\IPasswrd.Host.exe" "D:\MyProjects\IPasswrd\dist\IPasswrd.Host.exe" >> "D:\MyProjects\IPasswrd\dist-publish.log" 2>&1
