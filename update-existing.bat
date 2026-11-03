@echo off
chcp 936 >nul
title AI-ACE 更新已有部署
setlocal enabledelayedexpansion
set "FAIL=0"
set "FAST="
set "PKG="
:args
if "%~1"=="" goto :argsdone
if /i "%~1"=="-fast" set "FAST=1"
if /i "%~1"=="-pkg" (
  set "PKG=%~2"
  shift
)
shift
goto :args
:argsdone
if defined PKG if "%PKG:~-1%"=="\" set "PKG=%PKG:~0,-1%"
if not defined PKG cd /d "%~dp0"
echo ============================================================
echo   AI-ACE 更新已有部署: 旧版 -^> 离线合规版
echo   只停栈 / 拷文件 / 校验 / 起栈 / 自检, 不删除任何数据
echo ============================================================
echo.
if not exist "compose\docker-compose.yml" (
  echo [FAIL] 当前目录没有 compose\docker-compose.yml
  echo        当前目录: %CD%
  echo        请在 AI-ACE 根目录运行本脚本, 或双击升级包里的 一键升级.bat
  goto :end
)
docker version >nul 2>&1
if errorlevel 1 (
  echo [FAIL] 找不到可用的 Docker, 请先启动 Docker Desktop
  goto :end
)
if defined PKG (
  if not exist "%PKG%\compose\docker-compose.yml" (
    echo [FAIL] 升级包里缺 compose\docker-compose.yml
    echo        升级包: %PKG%
    set "FAIL=1"
    goto :end
  )
  if not exist "%PKG%\models\whisper\base\model.bin" (
    echo [FAIL] 升级包里缺 models\whisper\base\model.bin
    echo        升级包: %PKG%
    set "FAIL=1"
    goto :end
  )
)
echo [1/8] 当前容器状态
docker compose -f compose/docker-compose.yml ps
echo.
echo [2/8] 备份旧 compose 并停止容器
rem 约束: 部署机不留副本, 不再自动备份 compose
docker compose -f compose/docker-compose.yml down
if errorlevel 1 (
  echo [FAIL] 停栈失败, 请人工检查后再试
  set "FAIL=1"
  goto :end
)
echo   [ OK ] 已停止 (按约束不留 compose 副本)
echo.
echo [3/8] 放入增量文件
if defined PKG (
  copy /Y "%PKG%\compose\docker-compose.yml" "compose\docker-compose.yml" >nul
  copy /Y "%PKG%\compose\offline_probe.py" "compose\offline_probe.py" >nul
  copy /Y "%PKG%\check-offline.bat" "check-offline.bat" >nul
  copy /Y "%PKG%\update-existing.bat" "update-existing.bat" >nul
  xcopy /E /I /Y "%PKG%\models\whisper" "models\whisper" >nul
  xcopy /E /I /Y "%PKG%\open-webui\cache\tiktoken" "open-webui\cache\tiktoken" >nul
  echo   [ OK ] 已从升级包自动拷贝
  echo         compose 2 个 / bat 2 个 / whisper 模型 4 个 / tiktoken 词表 1 个
) else (
  echo   请手工把下列文件拷进去, 拷好后按任意键继续
  echo.
  echo   compose\docker-compose.yml          覆盖
  echo   compose\offline_probe.py            新增
  echo   check-offline.bat                   新增
  echo   models\whisper\base\model.bin       145 MB
  echo   models\whisper\base\tokenizer.json  2.2 MB
  echo   models\whisper\base\vocabulary.txt  0.5 MB
  echo   models\whisper\base\config.json     2 KB
  echo   open-webui\cache\tiktoken\9b5ad71b2ce5302211f9c61530b329a4922fc6a4
  echo.
  echo   不要覆盖: webui.db  uploads  vector_db  documents  output
  if not defined FAST pause
)
echo.
echo [4/8] 校验文件与镜像
if exist "compose\docker-compose.yml" (echo   [ OK ] compose\docker-compose.yml) else (echo   [FAIL] 缺 compose\docker-compose.yml & set "FAIL=1")
if exist "compose\offline_probe.py" (echo   [ OK ] compose\offline_probe.py) else (echo   [FAIL] 缺 compose\offline_probe.py & set "FAIL=1")
if exist "models\whisper\base\model.bin" (echo   [ OK ] models\whisper\base\model.bin) else (echo   [FAIL] 缺 models\whisper\base\model.bin & set "FAIL=1")
if exist "models\whisper\base\tokenizer.json" (echo   [ OK ] models\whisper\base\tokenizer.json) else (echo   [FAIL] 缺 models\whisper\base\tokenizer.json & set "FAIL=1")
if exist "models\whisper\base\vocabulary.txt" (echo   [ OK ] models\whisper\base\vocabulary.txt) else (echo   [FAIL] 缺 models\whisper\base\vocabulary.txt & set "FAIL=1")
if exist "models\whisper\base\config.json" (echo   [ OK ] models\whisper\base\config.json) else (echo   [FAIL] 缺 models\whisper\base\config.json & set "FAIL=1")
if exist "open-webui\cache\tiktoken\9b5ad71b2ce5302211f9c61530b329a4922fc6a4" (echo   [ OK ] tiktoken 词表缓存) else (echo   [FAIL] 缺 tiktoken 词表缓存 & set "FAIL=1")
findstr /c:"ENABLE_PERSISTENT_CONFIG" "compose\docker-compose.yml" >nul 2>&1
if errorlevel 1 (echo   [FAIL] compose 不是新版, 可能拷错文件 & set "FAIL=1") else (echo   [ OK ] compose 是新版)
findstr /c:"OFFLINE_MODE" "compose\docker-compose.yml" >nul 2>&1
if errorlevel 1 (echo   [FAIL] compose 里没有 OFFLINE_MODE & set "FAIL=1") else (echo   [ OK ] compose 含离线开关)
for %%I in (
  ghcr.io/open-webui/open-webui:main
  qdrant/qdrant:latest
  ghcr.io/ggml-org/llama.cpp:server-cuda
) do (
  docker image inspect %%I >nul 2>&1
  if errorlevel 1 (echo   [FAIL] 缺镜像 %%I & set "FAIL=1") else (echo   [ OK ] 镜像 %%I)
)
echo.
if "%FAIL%"=="1" (
  echo [ABORT] 文件或镜像不齐, 未启动。补齐后重新运行本脚本
  echo         缺镜像时先 docker images 看实际 tag, 再 docker tag 成上面要求的名字
  goto :end
)
echo [5/8] 启动
docker compose -f compose/docker-compose.yml up -d --no-build
if errorlevel 1 (
  echo [FAIL] 启动失败, 请查看上面的报错
  set "FAIL=1"
  goto :end
)
echo   [ OK ] 已启动
echo.
echo [6/8] 等待服务就绪
if not defined FAST timeout /t 20 /nobreak >nul
docker compose -f compose/docker-compose.yml ps
echo.
echo [7/8] 容器内验证
docker exec open-webui printenv OFFLINE_MODE WHISPER_MODEL TIKTOKEN_CACHE_DIR
docker exec open-webui ls /models/whisper/base
docker exec open-webui python -c "from faster_whisper import WhisperModel; WhisperModel('/models/whisper/base', device='cpu', compute_type='int8'); print('MODEL OK')"
echo.
echo [8/8] 离线合规自检
if exist "check-offline.bat" (
  call check-offline.bat -y
) else (
  echo   [SKIP] 没找到 check-offline.bat
)
:end
echo.
echo ============================================================
echo 流程结束
echo   本机测试: 浏览器打开 http://127.0.0.1:3000
echo   麦克风: 点输入框右边的麦克风图标, 浏览器弹窗选允许, 说话应出实时字幕
echo   回滚: 按 更新已有部署.md 的对应章节手工删改 compose 里的几行
echo ============================================================
if not defined FAST pause
if "%FAIL%"=="1" exit /b 2
exit /b 0
