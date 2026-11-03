@echo off
chcp 936 >nul
setlocal
title AI-ACE
set "ROOT=%~dp0..\.."
pushd "%ROOT%"

echo 正在启动 AI-ACE, 请稍等...
echo.

docker version >nul 2>&1
if not errorlevel 1 goto :dockerd

echo [1/3] Docker 没在运行, 尝试启动 Docker Desktop...
if not exist "%ProgramFiles%\Docker\Docker\Docker Desktop.exe" goto :nodocker
start "" "%ProgramFiles%\Docker\Docker\Docker Desktop.exe"
set /a N=0
:waitdocker
ping -n 3 127.0.0.1 >nul
docker version >nul 2>&1
if not errorlevel 1 goto :dockerd
set /a N+=1
if %N% LSS 60 goto :waitdocker
echo [FAIL] Docker 等了两分钟还没起来, 请手动打开 Docker Desktop 再试.
pause
popd
exit /b 1

:nodocker
echo [FAIL] 这台机器上找不到 Docker Desktop.
echo         先手动打开 Docker Desktop, 等它显示 Running, 再运行本程序.
pause
popd
exit /b 1

:dockerd
echo [1/3] Docker 就绪
echo [2/3] 启动服务...
docker compose -f compose\docker-compose.yml up -d --no-build >nul 2>&1
if errorlevel 1 (
    echo [FAIL] 服务启动失败. 请运行 第三步-自检.bat 看是哪一项没起来.
    pause
    popd
    exit /b 1
)

echo [3/3] 等界面就绪...
set /a T=0
:waitweb
ping -n 3 127.0.0.1 >nul
powershell -NoProfile -Command "try{$r=Invoke-WebRequest -Uri 'http://127.0.0.1:3000/' -UseBasicParsing -TimeoutSec 3; if($r.StatusCode -ge 200){exit 0}else{exit 1}}catch{exit 1}" >nul 2>&1
if not errorlevel 1 goto :open
set /a T+=1
if %T% LSS 60 goto :waitweb
echo [WARN] 界面还没完全就绪, 先打开看看...

:open
set "EDGE=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if exist "%EDGE%" goto :openedge
set "EDGE=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
if exist "%EDGE%" goto :openedge
start "" http://127.0.0.1:3000
popd
exit /b 0

:openedge
start "" "%EDGE%" --app=http://127.0.0.1:3000 --user-data-dir="%ROOT%\.edge-app" --no-first-run --no-default-browser-check --window-size=1440,900
popd
exit /b 0
