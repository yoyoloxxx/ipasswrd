@echo off
rem Собирает установщик и пакет обновления через Velopack.
rem Использование: publish-installer.cmd [версия]   (по умолчанию 1.0.0)
rem
rem ВАЖНО: packId = IPasswrdApp, а НЕ IPasswrd. Velopack ставит программу в
rem %LocalAppData%\<packId>, а сейф лежит в %LocalAppData%\IPasswrd - при
rem совпадении имён удаление программы снесло бы сейф вместе с паролями.
rem
rem Single-file здесь ВЫКЛЮЧЕН намеренно: из одного 55-мегабайтного блоба
rem не выходит дельта-обновлений, качать пришлось бы всё целиком.
rem
rem ДЕЛЬТЫ ВКЛЮЧЕНЫ. 06.08.2026 их обвинили зря. Дельта действительно
rem скачивалась, а вставала предыдущая версия - но виноват был не пакет, а
rem ожидающий Update.exe: он вооружался конкретным релизом, тем, который
rem приложение нашло первым за запуск. Второй релиз того же вечера
rem скачивался и не применялся никогда. Починено в Updater.cs (вооружаемся
rem null - "применить самое новое из скачанного"), проверено 07.08.2026 на
rem живой установке: дельта 100 КБ вместо 90 МБ, 1.0.17 -> 1.0.18 встало.

setlocal
set VER=%1
if "%VER%"=="" set VER=1.0.0
set ROOT=D:\MyProjects\IPasswrd
set OUT=%ROOT%\build-vpk
set LOG=%ROOT%\installer-build.log
set VPK=%USERPROFILE%\.dotnet\tools\vpk.exe

echo === publish %VER% === > "%LOG%"
if exist "%OUT%" rmdir /S /Q "%OUT%"

"C:\Program Files\dotnet\dotnet.exe" publish "%ROOT%\src\IPasswrd.App\IPasswrd.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:Version=%VER% -o "%OUT%" >> "%LOG%" 2>&1
if errorlevel 1 goto fail

rem Расширение и нативный хост едут рядом с программой, чтобы кнопка
rem "Установить расширение" нашла всё в одной папке.
xcopy /E /I /Y "%ROOT%\extension" "%OUT%\extension" >> "%LOG%" 2>&1
if exist "%ROOT%\dist-host\IPasswrd.Host.exe" copy /Y "%ROOT%\dist-host\IPasswrd.Host.exe" "%OUT%\IPasswrd.Host.exe" >> "%LOG%" 2>&1

echo === pack === >> "%LOG%"
"%VPK%" pack --packId IPasswrdApp --packTitle IPasswrd --packAuthors "yoyoloxxx Dev" --packVersion %VER% --packDir "%OUT%" --mainExe IPasswrd.App.exe --icon "%ROOT%\src\IPasswrd.App\Assets\ipasswrd_app.ico" --outputDir "%ROOT%\Releases" >> "%LOG%" 2>&1
if errorlevel 1 goto fail

echo === OK === >> "%LOG%"
goto done

:fail
echo === FAILED === >> "%LOG%"

:done
