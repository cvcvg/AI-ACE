@echo off
chcp 936 >nul
setlocal
title 恢复联网

net session >nul 2>&1
if errorlevel 1 (
  echo 需要管理员权限，正在申请...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo   这个操作会把网卡重新打开，让这台机器可以上网。
echo   如果这台机器是正式部署机，用完请重新运行 第一步-断网.bat。
echo.
set /p OK=确认请输入大写 YES 然后回车，其他任意键取消: 
if /i not "%OK%"=="YES" (
  echo 已取消。
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0恢复联网.ps1" -Confirm
exit /b %ERRORLEVEL%
