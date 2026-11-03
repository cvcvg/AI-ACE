<#
.SYNOPSIS
  把 AI-ACE 的数据目录搬到别的盘(默认 D:\AI-ACE-data), 并写 compose\.env。

.DESCRIPTION
  数据目录含 5 个子目录: open-webui / documents / output / knowledge / database。
  老安装里它们就在安装目录下; 本脚本把它们整体复制到新位置, 校验文件数与字节数一致后,
  写 <安装目录>\compose\.env 里的 AIACE_DATA=..., compose 下次启动就挂新位置。
  只复制、不删除: 源目录原样保留, 确认新位置能用之后再由你自己清理。

.PARAMETER From
  旧数据目录, 缺省 = 本脚本上一级目录(安装目录)。老机器的数据就在 <安装目录>\<子目录> 下。

.PARAMETER To
  新数据目录, 缺省 D:\AI-ACE-data。不能是 C 盘, 不能是盘根, 必须不存在或者是空目录。

.PARAMETER DryRun
  只打印计划, 不复制、不写 .env、不改任何文件。

.EXAMPLE
  数据迁移.bat -DryRun

.EXAMPLE
  数据迁移.bat -From G:\AI-ACE -To E:\AI-ACE-data
#>
[CmdletBinding()]
param(
    [string]$From = '',
    [string]$To = 'D:\AI-ACE-data',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$SubDirs = @('open-webui', 'documents', 'output', 'knowledge', 'database')

function Stop-WithError {
    param([string]$Message)
    Write-Host ''
    Write-Host ('[失败] ' + $Message) -ForegroundColor Red
    Write-Host '没有删除、也没有改动任何原有数据。按上面的提示修好后重跑本脚本即可。' -ForegroundColor Yellow
    exit 1
}

function Get-TreeStat {
    param([string]$Path)
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
    [long]$bytes = 0
    foreach ($f in $files) {
        $bytes = $bytes + $f.Length
    }
    return [pscustomobject]@{ Count = $files.Count; Bytes = $bytes }
}

# ---- 路径解析 ----
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Split-Path -Parent $scriptDir
if ([string]::IsNullOrWhiteSpace($From)) {
    $From = $installDir
}

if ($To -match '^[A-Za-z]:\\?$') {
    Stop-WithError ('目标不能是盘根, 请给它一个专用子目录, 例如 D:\AI-ACE-data: ' + $To)
}

try {
    $fromFull = [System.IO.Path]::GetFullPath($From).TrimEnd('\')
    $toFull = [System.IO.Path]::GetFullPath($To).TrimEnd('\')
}
catch {
    Stop-WithError ('路径不合法: From=' + $From + ' To=' + $To)
}

$toRoot = [System.IO.Path]::GetPathRoot($toFull)
if ($toRoot -match '^[Cc]:') {
    Stop-WithError ('目标不能在 C 盘, C 盘会被系统重装/清理波及: ' + $toFull)
}
if ($toFull -eq $toRoot.TrimEnd('\')) {
    Stop-WithError ('目标不能是盘根, 请给它一个专用子目录, 例如 D:\AI-ACE-data: ' + $toFull)
}

if (-not (Test-Path -LiteralPath $fromFull -PathType Container)) {
    Stop-WithError ('旧数据目录不存在: ' + $fromFull)
}
$fromCmp = $fromFull.ToLowerInvariant()
$toCmp = $toFull.ToLowerInvariant()
if ($fromCmp -eq $toCmp) {
    Stop-WithError '新旧数据目录不能是同一个目录。'
}
if ($toCmp.StartsWith($fromCmp + '\')) {
    Stop-WithError ('目标不能放在旧数据目录里面, 否则会套娃复制: ' + $toFull)
}
if ($fromCmp.StartsWith($toCmp + '\')) {
    Stop-WithError ('旧数据目录不能放在目标目录里面: ' + $fromFull)
}

$toExists = Test-Path -LiteralPath $toFull -PathType Container
if ($toExists) {
    $existing = @(Get-ChildItem -LiteralPath $toFull -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Stop-WithError ('目标目录不是空的, 为免覆盖已有数据请换一个空目录: ' + $toFull)
    }
}

$present = @()
foreach ($sub in $SubDirs) {
    if (Test-Path -LiteralPath (Join-Path $fromFull $sub) -PathType Container) {
        $present += $sub
    }
}
if ($present.Count -eq 0) {
    Stop-WithError ('在 ' + $fromFull + ' 下没找到任何数据子目录(' + ($SubDirs -join ', ') + ')。请用 -From 指向正确的旧数据目录。')
}

$composeDir = Join-Path $installDir 'compose'
$envPath = Join-Path $composeDir '.env'
$envValue = $toFull -replace '\\', '/'
$envLine = 'AIACE_DATA=' + $envValue

# ---- 计划 ----
Write-Host 'AI-ACE 数据迁移' -ForegroundColor Cyan
Write-Host ('  旧数据目录 : ' + $fromFull)
Write-Host ('  新数据目录 : ' + $toFull + '  (' + $(if ($toExists) { '已存在, 空目录' } else { '尚不存在, 自动创建' }) + ')')
Write-Host ('  配置文件   : ' + $envPath)
Write-Host ('  写入内容   : ' + $envLine)
Write-Host '  子目录     :'
foreach ($sub in $SubDirs) {
    if ($present -notcontains $sub) {
        Write-Host ('    - ' + $sub + ': 源目录不存在, 跳过')
        continue
    }
    $st = Get-TreeStat -Path (Join-Path $fromFull $sub)
    Write-Host ('    - ' + $sub + ': ' + $st.Count + ' 个文件 / ' + $st.Bytes + ' 字节')
}

if ($DryRun) {
    Write-Host ''
    Write-Host '[DryRun] 以上只是计划: 没有复制、没有创建目录、没有写 ' -NoNewline
    Write-Host $envPath -NoNewline
    Write-Host '。去掉 -DryRun 再跑一次才会真正执行。' 
    exit 0
}

# ---- 复制 ----
try {
    foreach ($sub in $SubDirs) {
        if ($present -notcontains $sub) { continue }
        $src = Join-Path $fromFull $sub
        $dst = Join-Path $toFull $sub
        Write-Host ('[复制] ' + $src + '  ->  ' + $dst)
        if (-not (Test-Path -LiteralPath $dst -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $dst -Force
        }
        & robocopy $src $dst /E /COPY:DAT /DCOPY:T /R:1 /W:1 /MT:16 /NFL /NDL /NJH /NP | Out-Null
        $rc = $LASTEXITCODE
        if ($rc -ge 8) {
            Stop-WithError ('robocopy 复制失败(退出码 ' + $rc + '): ' + $src + ' -> ' + $dst)
        }
    }
}
catch {
    Stop-WithError ('复制过程中出错: ' + $_.Exception.Message)
}

# ---- 校验 ----
Write-Host '[校验] 逐目录比对文件数与字节数 ...'
$bad = @()
foreach ($sub in $present) {
    $a = Get-TreeStat -Path (Join-Path $fromFull $sub)
    $b = Get-TreeStat -Path (Join-Path $toFull $sub)
    if (($a.Count -ne $b.Count) -or ($a.Bytes -ne $b.Bytes)) {
        $bad += ('    ' + $sub + ': 源 ' + $a.Count + ' 文件/' + $a.Bytes + ' 字节, 目标 ' + $b.Count + ' 文件/' + $b.Bytes + ' 字节')
    }
    else {
        Write-Host ('  ok ' + $sub + ': ' + $a.Count + ' 个文件 / ' + $a.Bytes + ' 字节')
    }
}
if ($bad.Count -gt 0) {
    Stop-WithError ('复制结果与源不一致, 已中止(不会写 .env):' + [Environment]::NewLine + ($bad -join [Environment]::NewLine) + [Environment]::NewLine + '新目录里的半成品可以整个删掉, 源数据一个字节都没动。')
}

# ---- 写 compose.env ----
if (-not (Test-Path -LiteralPath $composeDir -PathType Container)) {
    Stop-WithError ('找不到 compose 目录, 数据已复制好但没有改配置: ' + $composeDir)
}
$keep = @()
if (Test-Path -LiteralPath $envPath) {
    $old = @(Get-Content -LiteralPath $envPath -Encoding UTF8 -ErrorAction SilentlyContinue)
    foreach ($l in $old) {
        if ($l -notmatch '^\s*AIACE_DATA\s*=') { $keep += $l }
    }
}
$outLines = @($envLine) + $keep
$text = ($outLines -join [Environment]::NewLine) + [Environment]::NewLine
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($envPath, $text, $utf8NoBom)
Write-Host ('[写入] ' + $envPath + '  ->  ' + $envLine)

# ---- 收尾 ----
Write-Host ''
Write-Host '[完成] 数据已复制并校验通过, compose 下次启动会挂新位置。' -ForegroundColor Green
Write-Host ('  新位置 : ' + $toFull)
Write-Host '  让配置生效: 先跑根目录的 stop-ai-ace.bat 再跑 start-ai-ace.bat(或直接重开 AI-ACE.exe)。'
Write-Host ('  旧数据仍在 ' + $fromFull + ', 确认新位置一切正常之后再手工删除, 建议先留几天。')
