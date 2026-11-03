@echo off
setlocal
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist build mkdir build
"%CSC%" /nologo /target:winexe /out:build\AI-ACE.exe /optimize+ ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
 /r:System.Web.Extensions.dll /r:System.Net.Http.dll ^
 /win32icon:icon.ico ^
 src\Models.cs src\Net.cs src\Ui.cs src\Boot.cs src\DataDir.cs src\Setup.cs src\NetGuard.cs src\MainForm.cs src\MessageView.cs src\Program.cs
if errorlevel 1 goto err
copy /Y icon.ico build\icon.ico >nul
if errorlevel 1 goto err
copy /Y build\AI-ACE.exe ..\AI-ACE.exe >nul
if errorlevel 1 goto err
echo BUILD OK
exit /b 0
:err
echo BUILD FAILED
exit /b 1
