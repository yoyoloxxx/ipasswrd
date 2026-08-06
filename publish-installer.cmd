@echo off
rem ����ࠥ� ��⠭��騪 � ����� ���������� �१ Velopack.
rem �ᯮ�짮�����: publish-installer.cmd [�����]   (�� 㬮�砭�� 1.0.0)
rem
rem �����: packId = IPasswrdApp, � �� IPasswrd. Velopack �⠢�� �ணࠬ�� �
rem %LocalAppData%\<packId>, � ᥩ� ����� � %LocalAppData%\IPasswrd - ��
rem ᮢ������� ��� 㤠����� �ணࠬ�� ᭥᫮ �� ᥩ� ����� � ��஫ﬨ.
rem
rem Single-file ����� �������� ����७��: �� ������ 55-�������⭮�� �����
rem �� ��室�� �����-����������, ����� ��諮�� �� ��� 楫����.
rem
rem ������ ���� ��������� (--delta None). �஢�७� 06.08.2026: �����
rem ᪠稢�����, �� �����쭮 � ����� ����� �� ᮡ�ࠥ���, � Update.exe
rem �ਬ���� ���������� ����� ����� �����. ������
rem "Update.exe apply --package <�����>" ��ࠡ��뢠�� ��୮ - �����
rem �������� ��� ᪠稢���� � �ਬ������. ���� �� ࠧ��ࠫ���, ��砥�
rem ����� 楫����: ��譨� ��䨪 �����⥭, ���������訩�� ��������
rem ��஫�� �㦥.

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

rem ����७�� � ��⨢�� ��� ���� �冷� � �ணࠬ���, �⮡� ������
rem "��⠭����� ���७��" ��諠 ��� � ����� �����.
xcopy /E /I /Y "%ROOT%\extension" "%OUT%\extension" >> "%LOG%" 2>&1
if exist "%ROOT%\dist-host\IPasswrd.Host.exe" copy /Y "%ROOT%\dist-host\IPasswrd.Host.exe" "%OUT%\IPasswrd.Host.exe" >> "%LOG%" 2>&1

echo === pack === >> "%LOG%"
"%VPK%" pack --packId IPasswrdApp --packTitle IPasswrd --packAuthors "yoyoloxxx Dev" --packVersion %VER% --packDir "%OUT%" --mainExe IPasswrd.App.exe --icon "%ROOT%\src\IPasswrd.App\Assets\ipasswrd_app.ico" --delta None --outputDir "%ROOT%\Releases" >> "%LOG%" 2>&1
if errorlevel 1 goto fail

echo === OK === >> "%LOG%"
goto done

:fail
echo === FAILED === >> "%LOG%"

:done
