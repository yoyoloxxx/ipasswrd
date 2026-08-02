@echo off
"C:\Program Files\dotnet\dotnet.exe" publish "D:\MyProjects\IPasswrd\src\IPasswrd.App\IPasswrd.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "D:\MyProjects\IPasswrd\dist" > "D:\MyProjects\IPasswrd\dist-publish.log" 2>&1
