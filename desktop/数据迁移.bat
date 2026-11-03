@echo off
rem AI-ACE data migration launcher (pure ASCII on purpose).
rem The target script is named in Chinese, so it is located with a wildcard
rem ("????.ps1" = exactly 4 characters, which only this script has) instead of
rem spelling the name out, then handed to powershell -File with %* passed through.
setlocal
set "PS1="
for %%F in ("%~dp0????.ps1") do if not defined PS1 set "PS1=%%~fF"
if not defined PS1 (
  echo [ERROR] Migration script not found next to this .bat.
  echo         Right-click the .ps1 in this folder and choose "Run with PowerShell".
  pause
  exit /b 1
)
echo Running: %PS1%
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
