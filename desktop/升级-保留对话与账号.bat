@echo off
for %%f in ("%~dp0*.ps1") do powershell -NoProfile -ExecutionPolicy Bypass -File "%%~ff" %*
echo.
pause
