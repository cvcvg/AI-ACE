@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-package.ps1" %*
echo.
pause
