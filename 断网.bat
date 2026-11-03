@echo off
chcp 936 >nul
title AI-ACE 永久断网
net session >nul 2>&1
if errorlevel 1 (
  echo 需要管理员权限, 正在申请提权 ...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo ============================================================
echo  这一步会让这台电脑**永久离线**:
echo    - 禁用所有物理网卡 (以太网 / 无线)
echo    - 加出站防火墙规则, 就算有人手动开网卡也发不出去
echo    - 关掉移动热点 / 遥测 / OneDrive / 跨设备剪贴板
echo  没有一键恢复. 以后要恢复只能管理员进 Windows 设置手工改.
echo ============================================================
echo.
echo 确认请输入大写 YES 然后回车, 其他任意键取消:
set "OK="
set /p OK=^> 
if /i not "%OK%"=="YES" (
  echo 已取消, 什么都没改.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0断网加固.ps1" -Confirm
echo.
echo 做完请跑一次 check-offline.bat 复查 (第 5 节应为 BLOCKED).
pause
