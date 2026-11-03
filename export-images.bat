@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo    AI-ACE 镜像导出器 (离线部署准备)
echo ============================================
echo.
echo 将所有镜像打包为单个 tar 文件, 供目标机器离线加载。
echo.

if not exist "docker-images" mkdir docker-images

echo 正在导出镜像到 docker-images\ai-ace-core.tar ...
docker save -o docker-images\ai-ace-core.tar ^
  qdrant/qdrant:latest ^
  ghcr.io/open-webui/open-webui:main ^
  ghcr.io/ggml-org/llama.cpp:server-cuda ^
  compose-ace-rag-service:latest ^
  compose-ace-document-parser:latest ^
  compose-ace-term-extractor:latest ^
  compose-ace-docx-generator:latest ^
  compose-ace-vision-service:latest ^
  compose-ace-dashboard:latest

if errorlevel 1 (
    echo.
    echo [错误] 导出失败。
    pause
    exit /b 1
)

echo.
echo 导出完成: docker-images\ai-ace-core.tar
echo 将该 tar 文件连同整个 G:\AI-ACE 目录拷贝到目标机器,
echo 在目标机器运行 load-images.bat 导入镜像。
pause
