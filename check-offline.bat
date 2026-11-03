@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"
set "FAIL="

echo ============================================================
echo   AI-ACE 离线合规自检
echo   目标: 推理全部本地完成 / 对话数据不出本机 / 容器零外联
echo ============================================================
echo.

echo [1/8] 容器状态
docker ps --format "  {{.Names}}  {{.Status}}" 2>nul
if errorlevel 1 (
  echo   [FAIL] 无法访问 Docker, 请确认 Docker Desktop 已启动
  set "FAIL=1"
)
echo.

echo [2/8] 必需镜像 (pull_policy: never, 缺一个就起不来)
for %%I in (qdrant/qdrant:latest ghcr.io/open-webui/open-webui:main ghcr.io/ggml-org/llama.cpp:server-cuda) do (
  docker image inspect %%I >nul 2>&1
  if errorlevel 1 (echo   [FAIL] 缺镜像 %%I & set "FAIL=1") else (echo   [ OK ] %%I)
)
for %%I in (compose-ace-dashboard:latest compose-ace-document-parser:latest compose-ace-term-extractor:latest compose-ace-rag-service:latest compose-ace-docx-generator:latest compose-ace-vision-service:latest) do (
  docker image inspect %%I >nul 2>&1
  if errorlevel 1 (echo   [FAIL] 缺镜像 %%I & set "FAIL=1") else (echo   [ OK ] %%I)
)
echo.

echo [3/8] 本地模型文件 (缺任何一个都会在运行时尝试联网)
for %%F in (
  "models\whisper\base\model.bin"
  "models\whisper\base\tokenizer.json"
  "models\whisper\base\vocabulary.txt"
  "models\whisper\base\config.json"
  "models\embedding\modules.json"
  "models\embedding\model.safetensors"
  "models\vision\Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf"
  "models\vision\mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf"
  "models\qwen3-4b\Qwen3-4B-Q4_K_M.gguf"
) do (
  if exist %%F (echo   [ OK ] %%~F) else (echo   [FAIL] 缺失 %%~F & set "FAIL=1")
)
echo.

echo [4/8] tiktoken 词表缓存 (缺失会去 openaipublic.blob.core.windows.net 下载)
if exist "open-webui\cache\tiktoken\9b5ad71b2ce5302211f9c61530b329a4922fc6a4" (
  echo   [ OK ] cl100k_base 1681126 bytes
) else (
  echo   [FAIL] 缺 open-webui\cache\tiktoken\9b5ad71b2ce5302211f9c61530b329a4922fc6a4
  set "FAIL=1"
)
echo.

echo [5/8] 断网核查 (容器探测 + 物理网卡 + 防火墙)
docker cp "compose\offline_probe.py" open-webui:/tmp/offline_probe.py >nul 2>&1
if errorlevel 1 (
  echo   [SKIP] open-webui 未运行, 跳过
) else (
  docker exec open-webui python /tmp/offline_probe.py
  if errorlevel 3 (
    echo   [FAIL] 容器仍能访问公网: 请确认目标机已物理断网, 或 Linux 上用 internal: true
    set "FAIL=1"
  ) else (
    echo   [ OK ] 容器无法出网
  )
)
powershell -NoProfile -Command "if (@(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {$_.Status -eq 'Up'}).Count -eq 0) { exit 0 } else { exit 1 }" >nul 2>&1 && echo   [ OK ] 物理网卡全部禁用 || echo   [warn] 物理网卡在用, 正常联网模式, 数据隔离由容器层保证
netsh advfirewall firewall show rule name=all 2>nul | findstr /i /c:"AI-ACE-" >nul 2>&1 && echo   [ OK ] 出站阻断规则已就位 || echo   [warn] 没有出站阻断规则, 可选, 见 断网.bat
powershell -NoProfile -Command "if (@(Get-NetFirewallProfile -ErrorAction SilentlyContinue | Where-Object {$_.DefaultOutboundAction -ne 'Block'}).Count -eq 0) { exit 0 } else { exit 1 }" >nul 2>&1 && echo   [ OK ] 出站白名单已启用 || echo   [warn] 出站白名单未启用, 可选: 运行 防火墙-只走内网.bat
echo.

echo [6/8] 关键开关与日志痕迹
findstr /c:"QDRANT__TELEMETRY_DISABLED" "compose\docker-compose.yml" >nul 2>&1 && echo   [ OK ] Qdrant 遥测已关闭 || (echo   [FAIL] compose 缺 QDRANT__TELEMETRY_DISABLED & set "FAIL=1")
findstr /c:"OFFLINE_MODE" "compose\docker-compose.yml" >nul 2>&1 && echo   [ OK ] Open WebUI OFFLINE_MODE 已开 || (echo   [FAIL] compose 缺 OFFLINE_MODE & set "FAIL=1")
set "ACEPS=%~dp0ace-ps.tmp"
docker ps --format "{{.Names}}" >"%ACEPS%" 2>nul
if exist "%ACEPS%" (
  findstr /c:"ace-file-tools" "%ACEPS%" >nul && echo   [ OK ] ace-file-tools 运行中 || (echo   [FAIL] ace-file-tools 未运行 & set "FAIL=1")
  findstr /c:"ace-agent" "%ACEPS%" >nul && echo   [ OK ] ace-agent 运行中 || (echo   [FAIL] ace-agent 未运行 & set "FAIL=1")
  del "%ACEPS%" >nul 2>&1
) else (
  echo   [FAIL] 无法获取容器列表
  set "FAIL=1"
)
docker exec open-webui python -c "import urllib.request;print(urllib.request.urlopen('http://ace-file-tools:8010/health',timeout=5).status)" >nul 2>&1 && echo   [ OK ] 本地文件工具服务可达 || (echo   [WARN] 文件工具暂不可达, 首次启动可能较慢)
docker exec open-webui python -c "import urllib.request;print(urllib.request.urlopen('http://ace-agent:8011/health',timeout=5).status)" >nul 2>&1 && echo   [ OK ] 自动执行器可达 || (echo   [WARN] 自动执行器暂不可达, 首次启动可能较慢)
set "ACEOPS=%~dp0ace-ops.tmp"
docker exec ace-file-tools python -c "import json,urllib.request;print(len(json.load(urllib.request.urlopen('http://localhost:8010/openapi.json',timeout=5)).get('paths',{})))" >"%ACEOPS%" 2>nul
findstr /c:"12" "%ACEOPS%" >nul && echo   [ OK ] 文件工具已暴露 12 个工具, 含全自动执行 || (echo   [WARN] 文件工具工具数不是 12, 源码可能没更新)
del "%ACEOPS%" >nul 2>&1
docker exec ace-file-tools python -c "import pathlib;d=pathlib.Path('/work');d.mkdir(parents=True,exist_ok=True);f=d/'.rw-probe';f.write_text('ok');f.unlink()" >nul 2>&1 && echo   [ OK ] 工作目录可写: 模型能写文件并生成 Word/Excel || (echo   [FAIL] 工作目录不可写, 检查 documents\output 挂载 & set "FAIL=1")
docker logs open-webui --tail 2000 2>&1 | findstr /i /c:"huggingface.co" /c:"openaipublic" /c:"api.openai.com" /c:"api.openwebui.com" /c:"pypi.org" >nul 2>&1
if errorlevel 1 (echo   [ OK ] 近 2000 行日志无外联尝试) else (echo   [WARN] 日志出现外联尝试, 请人工确认; 若为历史记录可忽略)

echo.
echo [7/8] 容器级隔离 (内网断出口 + 网关转发)
docker version >nul 2>&1
if errorlevel 1 (
  echo   [SKIP] Docker 不可用, 跳过
  goto :skipseven
)
docker ps --format "{{.Names}}" > "%~dp0ace-ps.tmp" 2>nul
findstr /c:"ace-gateway" "%~dp0ace-ps.tmp" >nul 2>&1 && echo   [ OK ] ace-gateway 运行中 || (echo   [FAIL] ace-gateway 未运行 & set "FAIL=1")
docker network inspect ai-private-net --format "{{.Internal}}" 2>nul | findstr /i "true" >nul && echo   [ OK ] ai-private-net 已隔离 (internal) || (echo   [FAIL] ai-private-net 不是 internal, 容器可出网 & set "FAIL=1")
docker exec open-webui python -c "import socket,sys;s=socket.socket();s.settimeout(3);sys.exit(0 if s.connect_ex(('1.1.1.1',443))==0 else 1)" >nul 2>&1
if errorlevel 1 (echo   [ OK ] 容器无法访问外网) else (echo   [FAIL] 容器仍能访问外网 & set "FAIL=1")
curl -s -o nul -w "%%{http_code}" --max-time 8 http://127.0.0.1:3000/ > "%~dp0ace-code.tmp" 2>nul
findstr /c:"200" "%~dp0ace-code.tmp" >nul 2>&1 && echo   [ OK ] 宿主机可经网关访问界面 (3000) || (echo   [FAIL] 宿主机访问 3000 失败 & set "FAIL=1")
del /q "%~dp0ace-code.tmp" >nul 2>&1
:skipseven
echo.

echo [8/8] 没有备份副本 (约束: 部署机不留任何副本)
dir /b "%~dp0compose\*.bak*" >nul 2>&1 && (echo   [FAIL] compose 目录有 .bak 副本, 按约束请删除 & set "FAIL=1") || echo   [ OK ] compose 目录无 .bak 副本
dir /b "%~dp0*.prev" >nul 2>&1 && (echo   [FAIL] 根目录有 .prev 副本, 按约束请删除 & set "FAIL=1") || echo   [ OK ] 根目录无 .prev 副本
dir /b "%~dp0*.bak" >nul 2>&1 && (echo   [WARN] 根目录还有 .bak 文件, 请确认是否需要 & set "FAIL=1") || echo   [ OK ] 根目录无 .bak 文件
echo.

echo ============================================================
if defined FAIL (
  echo   结果: 有项目未通过, 见上面 [FAIL]
echo   提示: [7/8] 容器级隔离必须全部 OK; [5/8] 的 warn 属正常联网模式
) else (
  echo   结果: 离线合规检查通过
)
echo ============================================================
if /i not "%~1"=="-y" pause
