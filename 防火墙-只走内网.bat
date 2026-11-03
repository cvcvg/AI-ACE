@echo off
chcp 936 >nul
setlocal
set "MODE=%~1"
title AI-ACE 出站白名单 (只走内网)
fltmc >nul 2>&1
if errorlevel 1 (
    echo 需要管理员权限, 正在请求提升...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%MODE%' -Verb RunAs" >nul 2>&1
    exit /b
)
if /i "%MODE%"=="-Restore" goto :restore
echo ============================================================
echo   出站白名单: 除本机和 Docker 虚拟网卡, 什么都发不出去
echo ============================================================
echo.
echo   做完之后:
echo     - AI-ACE 正常用  ^(它全部走 127.0.0.1^)
echo     - 这台机器插网线也发不出数据
echo     - 撤销: 右键本文件 -^> 运行, 或命令行加 -Restore
echo.
set /p GO=确认请手输大写 YES 再回车:
if not "%GO%"=="YES" (
    echo 已取消, 什么都没改.
    pause
    exit /b 1
)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0防火墙-只走内网.ps1" -Confirm
set RC=%ERRORLEVEL%
echo.
if "%RC%"=="0" (echo [ OK ] 出站白名单已启用.) else (echo [FAIL] 没启用成功, 返回码 %RC%)
pause
exit /b %RC%

:restore
echo 正在撤销出站白名单 (恢复 Windows 默认: 出站允许)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0防火墙-只走内网.ps1" -Restore
set RC=%ERRORLEVEL%
echo.
if "%RC%"=="0" (echo [ OK ] 已恢复.) else (echo [FAIL] 恢复失败, 返回码 %RC%)
pause
exit /b %RC%
