@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo    AI-ACE 镜像构建器 (仅首次部署需要, 需联网)
echo ============================================
echo.
echo 此脚本会构建全部服务镜像 (需下载基础镜像和依赖, 请耐心等待)。
echo 构建完成后请运行 export-images.bat 导出镜像, 供离线部署使用。
echo.

docker compose -f compose\docker-compose.yml build
if errorlevel 1 (
    echo.
    echo [错误] 构建失败。请检查网络后重试。
    pause
    exit /b 1
)

echo.
echo 构建完成。可运行 start-ai-ace.bat 启动系统。
pause
