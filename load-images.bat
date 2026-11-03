@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo    AI-ACE 离线镜像导入器
echo ============================================
echo.
echo 从 docker-images\ai-ace-core.tar 导入全部镜像 (完全离线, 不联网)。
echo.

if not exist "docker-images\ai-ace-core.tar" (
    echo [错误] 未找到 docker-images\ai-ace-core.tar
    echo 请先在有网络的机器上运行 export-images.bat 导出镜像,
    echo 并将导出的 tar 文件放到本目录的 docker-images\ 子目录下。
    pause
    exit /b 1
)

echo 正在导入镜像...
docker load -i docker-images\ai-ace-core.tar
if errorlevel 1 (
    echo.
    echo [错误] 导入失败。
    pause
    exit /b 1
)

echo.
echo 导入完成。现在可运行 start-ai-ace.bat 启动系统。
pause
