# ============================================================
#  AI-ACE 离线 Docker 镜像导入脚本
#  功能: 导入 docker-images 目录中的所有 .tar 镜像 (完全离线, 禁止联网)
#  用法: 右键"使用 PowerShell 运行" 或在 PowerShell 中执行
#        .\import-docker-images.ps1
# ============================================================
$ErrorActionPreference = "Stop"

# docker-images 目录位于本脚本的上级目录
$imgDir = Join-Path (Split-Path $PSScriptRoot -Parent) "docker-images"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "   AI-ACE 离线镜像导入器" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $imgDir)) {
    Write-Host "[错误] 未找到 docker-images 目录: $imgDir" -ForegroundColor Red
    Write-Host "请确认 docker-images 目录与本脚本的上级目录同级。" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

$tarFiles = Get-ChildItem -Path $imgDir -Filter "*.tar" | Sort-Object Name
if ($tarFiles.Count -eq 0) {
    Write-Host "[错误] docker-images 目录中没有 .tar 镜像文件" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}

Write-Host "镜像目录: $imgDir"
Write-Host "共找到 $($tarFiles.Count) 个镜像文件"
Write-Host ""

foreach ($tar in $tarFiles) {
    Write-Host ("正在导入: {0} ..." -f $tar.Name) -ForegroundColor Yellow
    docker load -i $tar.FullName
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("[错误] 导入 {0} 失败" -f $tar.Name) -ForegroundColor Red
        Read-Host "按回车退出"
        exit 1
    }
}

Write-Host ""
Write-Host "=== 全部镜像导入完成 ===" -ForegroundColor Green
Write-Host "现在可双击 start-ai-ace.bat 启动系统。"
Write-Host ""
Read-Host "按回车退出"
