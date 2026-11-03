@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo    AI-ACE 关闭器
echo ============================================
echo.
echo 正在安全关闭 AI-ACE 全部服务 (含模型容器)...
docker compose -f compose\docker-compose.yml --profile qwen3 --profile vision down
echo.
echo 已全部关闭。可再次双击 start-ai-ace.bat 启动。
echo.
pause
