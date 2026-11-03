# 制作精简部署包：只拷目标机真正需要的文件，逐项打印体积
param([string]$Dest = "", [switch]$WithImages)
$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Dest) { $Dest = Join-Path (Split-Path -Parent $src) "AI-ACE-部署包-精简" }
$root = Join-Path $Dest "AI-ACE"
New-Item -ItemType Directory -Force -Path $root | Out-Null

$dirs = @("models", "knowledge", "open-webui", "documents", "output", "database", "compose", "services", "vision-service", "desktop", "scripts", "docs")
$exclude = @("term-extractor", "cache", "logs", "backup", "doc-generator", "vision-git-temp", "vector_db")
if ($WithImages) { $dirs += "docker-images" }

foreach ($d in $dirs) {
  $from = Join-Path $src $d
  if (-not (Test-Path $from)) { continue }
  $to = Join-Path $root $d
  $xd = @()
  foreach ($e in $exclude) { $xd += (Join-Path $from $e) }
  $rc = @($from, $to, "/E", "/NFL", "/NDL", "/NJH", "/NP", "/R:1", "/W:1")
  if ($xd.Count -gt 0) { $rc += "/XD"; $rc += $xd }
  & robocopy @rc | Out-Null
}

# 包根 exe：用户双击的就是它；还没编译出来就跳过，不报错
$rootExe = Join-Path $src "AI-ACE.exe"
if (Test-Path $rootExe) {
  $rcExe = @($src, $root, "AI-ACE.exe", "/NFL", "/NDL", "/NJH", "/NP", "/R:1", "/W:1")
  & robocopy @rcExe | Out-Null
}

# 桌面端使用说明跟着包走（desktop 已整体拷过，这里再兜一次）
$readme = Join-Path $src "desktop\使用说明.txt"
if (Test-Path $readme) {
  New-Item -ItemType Directory -Force -Path (Join-Path $root "desktop") | Out-Null
  Copy-Item $readme -Destination (Join-Path $root "desktop\使用说明.txt") -Force
}

Get-ChildItem -Path $src -File | Where-Object { $_.Extension -in ".bat", ".ps1", ".md", ".txt" } | ForEach-Object {
  Copy-Item $_.FullName -Destination (Join-Path $root $_.Name) -Force
}

$bytes = (Get-ChildItem -Path $root -Recurse -File | Measure-Object -Property Length -Sum).Sum
$files = (Get-ChildItem -Path $root -Recurse -File).Count
"{0} 个文件，合计 {1:N2} GB" -f $files, ($bytes / 1GB)
"输出目录：$root"
"双击 $root\AI-ACE.exe 即可安装"