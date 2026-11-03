@echo off
rem AI-ACE network isolation - double-click friendly wrapper.
rem Finds the .ps1 next to this file and runs it. Extra args are passed through.
setlocal
cd /d "%~dp0"
set "AIACE_PS1="
for %%f in ("%~dp0*.ps1") do if not defined AIACE_PS1 set "AIACE_PS1=%%~ff"
if not defined AIACE_PS1 goto :nops1
echo.
echo   AI-ACE network isolation
echo   script : %AIACE_PS1%
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%AIACE_PS1%" %*
set "RC=%ERRORLEVEL%"
echo.
echo   exit code: %RC%   (0 = all green, 1 = risk items, 2 = failed)
echo.
echo   No argument   = audit only: writes a report, changes nothing.
echo   -Apply        = install the egress block rules (re-run after Docker Desktop restarts).
echo   -Remove       = roll the rules back.
echo   -Report       = full self-check incl. outbound probe.
echo   -DryRun       = print the plan, touch nothing.
echo.
pause
exit /b %RC%
:nops1
echo.
echo   [ERROR] No .ps1 found next to this .bat.
echo   Keep the script and this .bat in the same folder.
echo.
pause
exit /b 2
