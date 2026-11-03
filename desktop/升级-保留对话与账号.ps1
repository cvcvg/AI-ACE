<#
  就地升级：旧 AI-ACE -> 新部署包，只保留「账号 + 对话记录」，旧目录验证通过后自动删除
  用法（部署机上，双击同名 .bat 或命令行）：
    powershell -NoProfile -ExecutionPolicy Bypass -File 升级-保留对话与账号.ps1 -OldRoot "D:\AI-ACE" -Package "E:\AI-ACE-部署包-精简\AI-ACE"
  可选：
    -DryRun        只打印计划，不动任何文件
    -NoStart       不自动起服务（此时不删旧目录，等你手动确认后再删）
    -KeepOld       连询问都省掉，直接保留旧目录
    -Port 3000     健康检查端口
    -TimeoutSec 240 等待服务就绪的秒数
#>
param(
  [string]$OldRoot,
  [string]$Package,
  [switch]$DryRun,
  [switch]$NoStart,
  [switch]$KeepOld,
  [int]$Port = 3000,
  [int]$TimeoutSec = 240
)

$ErrorActionPreference = "Stop"
function Step($msg) { Write-Host ("==> " + $msg) -ForegroundColor Cyan }
function Die($msg) { Write-Host ("错误：" + $msg) -ForegroundColor Red; exit 1 }

if (-not $Package) { $Package = Split-Path -Parent $PSScriptRoot }
if (-not $OldRoot) {
  Write-Host "这个脚本跑在部署包里，会把旧 AI-ACE 的账号与对话搬过来。" -ForegroundColor Cyan
  Write-Host "请输入旧 AI-ACE 文件夹的路径（例如 D:\AI-ACE），直接回车用 D:\AI-ACE：" -ForegroundColor Cyan
  $OldRoot = Read-Host "旧目录"
  if (-not $OldRoot) { $OldRoot = "D:\AI-ACE" }
}
$OldRoot = $OldRoot.TrimEnd("\"); $Package = $Package.TrimEnd("\")
if (-not (Test-Path -LiteralPath $OldRoot)) { Die ("找不到旧目录：" + $OldRoot) }
if (-not (Test-Path -LiteralPath $Package)) { Die ("找不到部署包：" + $Package) }
$OldRoot = (Resolve-Path -LiteralPath $OldRoot).Path.TrimEnd("\")
$Package = (Resolve-Path -LiteralPath $Package).Path.TrimEnd("\")
if ($OldRoot -eq $Package) { Die "旧目录和部署包是同一个文件夹，没法就地升级" }

if (-not (Test-Path (Join-Path $OldRoot "compose\docker-compose.yml"))) { Die "旧目录不像 AI-ACE：找不到 compose\docker-compose.yml（$OldRoot）" }
if (-not (Test-Path (Join-Path $Package "compose\docker-compose.yml"))) { Die "部署包不像 AI-ACE：找不到 compose\docker-compose.yml（$Package）" }
if (-not (Test-Path (Join-Path $Package "models"))) { Die "部署包里没有 models 目录，模型没带全" }

$oldDb = Join-Path $OldRoot "open-webui\webui.db"
$hasDb = Test-Path $oldDb
if (-not $hasDb) { Write-Host "提示：旧目录里没有 open-webui\webui.db，将按全新部署处理（没有历史对话和账号）" -ForegroundColor Yellow }

$stamp  = Get-Date -Format "yyyyMMdd-HHmm"
$parent = Split-Path -Parent $OldRoot
$keep   = Join-Path $parent ("ai-ace-保留数据-" + $stamp)
$oldBak = $OldRoot + "-旧版-" + $stamp
$oldMB  = 0
try { $oldMB = [math]::Round(((Get-ChildItem $OldRoot -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB), 0) } catch {}

Step "旧目录：$OldRoot（约 $oldMB MB）"
Step "部署包：$Package"
Step "保留数据：$keep"
if ($DryRun) { Write-Host "（DryRun：只打印计划，不执行）" -ForegroundColor Yellow; exit 0 }

$pf = $env:ProgramFiles; if (-not $pf) { $pf = "C:\Program Files" }
$sysRoot = $env:SystemRoot; if (-not $sysRoot) { $sysRoot = "C:\Windows" }
$robocopy = Join-Path $sysRoot "System32\robocopy.exe"
if (-not (Test-Path $robocopy)) { $robocopy = "robocopy" }
$docker = "docker"
$cand = Join-Path $pf "Docker\Docker\resources\bin\docker.exe"
if (Test-Path $cand) { $docker = $cand }

Step "1/7 停止旧服务"
try { & $docker compose -f (Join-Path $OldRoot "compose\docker-compose.yml") down --remove-orphans 2>$null | Out-Null }
catch { Write-Host "    停服务失败（Docker 可能没启动），继续" -ForegroundColor Yellow }
Start-Sleep -Seconds 3

Step "2/7 备份账号与对话记录"
New-Item -ItemType Directory -Force -Path (Join-Path $keep "open-webui") | Out-Null
if ($hasDb) {
  foreach ($f in @("webui.db", "webui.db-wal", "webui.db-shm")) {
    $p = Join-Path $OldRoot ("open-webui\" + $f)
    if (Test-Path $p) { Copy-Item $p (Join-Path $keep "open-webui") -Force }
  }
}
$up = Join-Path $OldRoot "open-webui\uploads"
if (Test-Path $up) { & $robocopy $up (Join-Path $keep "open-webui\uploads") /E /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Null }
$keptMB = [math]::Round(((Get-ChildItem $keep -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    已备份 $keptMB MB -> $keep"

Step "3/7 旧目录改名"
Rename-Item -LiteralPath $OldRoot -NewName (Split-Path -Leaf $oldBak) -Force

Step "4/7 展开新部署包"
New-Item -ItemType Directory -Force -Path $OldRoot | Out-Null
& $robocopy $Package $OldRoot /E /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Null
if (-not (Test-Path (Join-Path $OldRoot "compose\docker-compose.yml"))) { Die "展开失败：新目录里没有 compose\docker-compose.yml" }

Step "5/7 放回账号与对话记录"
& $robocopy (Join-Path $keep "open-webui") (Join-Path $OldRoot "open-webui") /E /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Null

function Remove-Tree($path) {
  try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop; return $true }
  catch {
    $tmpRoot = $env:TEMP; if (-not $tmpRoot) { $tmpRoot = [System.IO.Path]::GetTempPath() }
    $empty = Join-Path $tmpRoot ("ai-ace-empty-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $empty | Out-Null
    & $robocopy $empty $path /MIR /NFL /NDL /NJH /NP /R:1 /W:1 | Out-Null
    Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
    try { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop; return $true } catch { return $false }
  }
}

Step "6/7 启动并等待服务"
$ok = $false
if ($NoStart) {
  Write-Host "    已跳过启动（-NoStart）" -ForegroundColor Yellow
} else {
  $startBat = Join-Path $OldRoot "start-ai-ace.bat"
  if (Test-Path $startBat) { Start-Process -FilePath $startBat -WorkingDirectory $OldRoot | Out-Null }
  else { Write-Host "    找不到 start-ai-ace.bat，请手动启动" -ForegroundColor Yellow }
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    try {
      $r = Invoke-WebRequest -Uri ("http://127.0.0.1:" + $Port + "/health") -UseBasicParsing -TimeoutSec 5
      if ($r.StatusCode -eq 200) { $ok = $true; break }
    } catch {
      try {
        $r = Invoke-WebRequest -Uri ("http://127.0.0.1:" + $Port + "/") -UseBasicParsing -TimeoutSec 5
        if ($r.StatusCode -eq 200) { $ok = $true; break }
      } catch { }
    }
    Start-Sleep -Seconds 5
  }
  if ($ok) { Write-Host "    服务已就绪：http://127.0.0.1:$Port" -ForegroundColor Green }
  else { Write-Host "    $TimeoutSec 秒内没起来（首次启动要解压/加载模型可能更久）" -ForegroundColor Yellow }
}

Step "7/7 清理旧目录"
if ($KeepOld) {
  Write-Host "    按 -KeepOld 保留：$oldBak" -ForegroundColor Yellow
} elseif ($NoStart) {
  Write-Host "    未验证新系统（-NoStart），先保留：$oldBak" -ForegroundColor Yellow
  Write-Host "    确认能登录、历史对话都在之后，手动删掉它即可回收空间" -ForegroundColor Yellow
} elseif (-not $ok) {
  Write-Host "    新系统没确认就绪，旧目录不删：$oldBak" -ForegroundColor Yellow
  Write-Host "    排查办法：看 AI-ACE\logs、docker ps；确认没问题后再删它" -ForegroundColor Yellow
} else {
  Write-Host ""
  Write-Host ("    旧目录占用约 {0} MB：{1}" -f $oldMB, $oldBak) -ForegroundColor Yellow
  Write-Host "    账号与对话已在第 2 步备份，删掉旧目录只会回收空间，不影响使用。" -ForegroundColor Yellow
  $ans = Read-Host "    确认删除旧目录吗？输入 Y 回车 = 删除 / 直接回车 = 保留"
  if ($ans -match '^[Yy]') {
    Write-Host "    正在删除，文件多时需要一会儿…" -ForegroundColor Cyan
    if (Remove-Tree $oldBak) { Write-Host ("    已删除，回收约 {0} MB" -f $oldMB) -ForegroundColor Green }
    else { Write-Host "    自动删除失败（文件被占用或权限不足），手动删：$oldBak" -ForegroundColor Yellow }
  } else {
    Write-Host "    已保留：$oldBak（想删时右键删掉即可）" -ForegroundColor Yellow
  }
}

Write-Host ""
Write-Host "新目录：$OldRoot" -ForegroundColor Green
Write-Host "账号与对话保留副本：$keep（几十 MB，确认系统正常后可删）" -ForegroundColor Green
if (-not $NoStart) { Write-Host "浏览器打开 http://127.0.0.1:$Port ，用原账号登录，历史对话应还在" -ForegroundColor Green }
if (-not $hasDb) { Write-Host "注意：这次没有旧库可迁，账号需要重新注册" -ForegroundColor Yellow }
