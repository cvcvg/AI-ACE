@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ============================================
echo    AI-ACE 一键启动器 (完全离线)
echo ============================================
echo.

REM ---------- 1. 检查 Docker Desktop ----------
docker info >nul 2>&1
if errorlevel 1 (
    echo [1/5] Docker Desktop 未运行, 正在启动...
    set "DOCKER_EXE=C:\Program Files\Docker\Docker\Docker Desktop.exe"
    if exist "!DOCKER_EXE!" (
        start "" "!DOCKER_EXE!"
    ) else (
        echo [提示] 未找到 Docker Desktop, 请手动启动后重试。
    )
    set /a count=0
    :wait_docker
    timeout /t 5 /nobreak >nul
    docker info >nul 2>&1
    if not errorlevel 1 goto docker_ready
    set /a count+=1
    if !count! geq 24 (
        echo [错误] Docker Desktop 启动超时。请确认 Docker 已安装并手动启动。
        pause
        exit /b 1
    )
    goto wait_docker
)
:docker_ready
echo [1/5] Docker Desktop 已就绪
echo.

REM ---------- 2. 检查 NVIDIA GPU ----------
echo [2/5] 检查 NVIDIA GPU...
nvidia-smi >nul 2>&1
if errorlevel 1 (
    echo [警告] 未检测到 NVIDIA GPU 或驱动。文本模型仍可用, 但 GPU 加速和视觉模型不可用。
) else (
    echo        NVIDIA GPU 已检测到
)
if not exist "system" mkdir system
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $g = nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits | Select-Object -First 1; if ($g) { $p = (($g -split ',')[0]).Trim(); $m = [int]((($g -split ',')[1]).Trim()); @{name=$p; memory_total_mb=$m} | ConvertTo-Json | Out-File -Encoding utf8 'system\gpu.json' } } catch { }"
echo.

REM ---------- 3. 检查镜像是否已导入 ----------
echo [3/5] 检查镜像是否已导入...
set "IMG_MISSING=0"
for %%I in (qdrant/qdrant:latest ghcr.io/open-webui/open-webui:main ghcr.io/ggml-org/llama.cpp:server-cuda) do (
    docker image inspect %%I >nul 2>&1
    if errorlevel 1 (
        echo   [缺少] %%I
        set "IMG_MISSING=1"
    )
)
if "!IMG_MISSING!"=="1" (
    echo.
    echo [错误] 检测到镜像缺失。请先运行 scripts\import-docker-images.ps1 导入离线镜像。
    pause
    exit /b 1
)
echo        镜像检查通过
echo.

REM ---------- 4. 启动核心服务 (禁止 pull / build) ----------
echo [4/5] 启动核心服务 (离线, 禁止联网拉取)...
docker compose -f compose\docker-compose.yml up -d --no-build
if errorlevel 1 (
    echo [错误] 启动失败, 请查看上方日志。
    pause
    exit /b 1
)
echo.

REM ---------- 5. 创建模型容器 + 等待健康 ----------
echo [5/5] 创建模型容器并等待服务就绪...
docker compose -f compose\docker-compose.yml --profile qwen3 --profile vision create qwen3-text-service vision-service-model

echo.
echo 正在等待服务健康...
set /a wait=0
:wait_health
timeout /t 5 /nobreak >nul
curl -s http://127.0.0.1:3000 >nul 2>&1
if not errorlevel 1 goto health_ok
set /a wait+=1
if !wait! geq 24 goto started
goto wait_health
:health_ok
echo        服务已就绪
:started

echo.
echo ============================================
echo    AI-ACE 启动完成!
echo    聊天界面:  http://127.0.0.1:3000
echo    控制中心:  http://127.0.0.1:9000
echo ============================================
echo.
echo 正在打开聊天界面...
start http://127.0.0.1:3000
timeout /t 3 >nul
exit /b 0
