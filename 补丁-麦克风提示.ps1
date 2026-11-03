# =====================================================================
# 给"已部署的 open-webui 镜像"打麦克风错误提示补丁
#
# 背景: 官方镜像把任何 getUserMedia 失败都显示为 "Permission denied ...",
#       导致"没插麦克风""麦克风被占用"也被误报成"权限被拒"。
# 本脚本把这段提示改成按 DOMException.name 分类:
#       NotAllowedError  -> 权限被拒(原文案)
#       NotFoundError    -> 未检测到设备
#       NotReadableError -> 被其他程序占用
#       其它             -> 带真实 name/message
#
# 用法(在有该镜像的机器上, 需已 docker load / pull):
#   powershell -ExecutionPolicy Bypass -File .\补丁-麦克风提示.ps1
#   powershell -ExecutionPolicy Bypass -File .\补丁-麦克风提示.ps1 -Image ghcr.io/open-webui/open-webui:main -NewTag ace/open-webui:mic-fix
#
# 打完补丁后, 把 compose 里 open-webui 的 image 改成 -NewTag 指定的名字,
# 然后 docker compose up -d --no-build open-webui 重建容器即可。
# 离线部署时记得重新 docker save 这个新镜像。
#
# -PatchDir 模式: 直接对本地解包出来的 build 目录打补丁(用于测试或预烘焙)
#   powershell -ExecutionPolicy Bypass -File .\补丁-麦克风提示.ps1 -PatchDir D:\tmp\build
# =====================================================================
param(
    [string]$Image    = 'ghcr.io/open-webui/open-webui:main',
    [string]$NewTag   = 'ace/open-webui:mic-fix',
    [string]$PatchDir = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$Pattern = '([A-Za-z_$][\w$]*)\.error\(([A-Za-z_$][\w$]*)\(\)\.t\("Permission denied when accessing microphone: \{\{error\}\}",\{error:([A-Za-z_$][\w$]*)\}\)\)'
$Marker  = '未检测到可用的麦克风设备'

function New-Replacement([string]$toast, [string]$i18n, [string]$v) {
    # 用单引号片段拼接: 避免在 PowerShell 里转义 JS 的双引号(上一版就是在转义上出错的)
    $parts = @(
        $toast, '.error(',
        $v, '&&', $v, '.name==="NotAllowedError"?',
        $i18n, '().t("Permission denied when accessing microphone: {{error}}",{error:', $v, '})',
        ':', $v, '&&', $v, '.name==="NotFoundError"?',
        '"麦克风: 未检测到可用的麦克风设备 (NotFoundError), 请确认设备已连接且被系统识别"',
        ':', $v, '&&(', $v, '.name==="NotReadableError"||', $v, '.name==="AbortError")?',
        '"麦克风被其他程序或标签页占用 ("+', $v, '.name+"), 请关闭后重试"',
        ':"麦克风访问失败: "+((', $v, '&&', $v, '.name)||"?")+": "+((', $v, '&&', $v, '.message)||', $v, '))'
    )
    return ($parts -join '')
}

# 返回 $null 表示该文件无需/无法打补丁
function Patch-Text([string]$text) {
    if ($text.Contains($Marker)) { return $null }        # 已经打过
    if (-not [regex]::IsMatch($text, $Pattern)) { return $null }
    $evaluator = [System.Text.RegularExpressions.MatchEvaluator] {
        param($m)
        New-Replacement $m.Groups[1].Value $m.Groups[2].Value $m.Groups[3].Value
    }
    return [regex]::Replace($text, $Pattern, $evaluator)
}

function Patch-File([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $text  = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes)
    $new   = Patch-Text $text
    if ($null -eq $new) { return $false }
    if (-not $DryRun) {
        [IO.File]::WriteAllBytes($path, (New-Object System.Text.UTF8Encoding($false)).GetBytes($new))
    }
    return $true
}

# ---------------- 模式一: 直接补丁本地目录 ----------------
if ($PatchDir -ne '') {
    if (-not (Test-Path $PatchDir)) { throw "目录不存在: $PatchDir" }
    $hit = 0
    Get-ChildItem -Path $PatchDir -Recurse -Filter *.js -File | ForEach-Object {
        if (Patch-File $_.FullName) {
            $hit++
            Write-Host ("[OK] " + $_.FullName)
        }
    }
    Write-Host "完成: 修改 $hit 个文件 (PatchDir=$PatchDir)"
    if ($hit -eq 0) { Write-Host "提示: 没有匹配到目标代码, 镜像内前端版本可能与本脚本预期不同。" }
    exit 0
}

# ---------------- 模式二: 补丁容器镜像 ----------------
docker image inspect $Image *> $null
if ($LASTEXITCODE -ne 0) { throw "本机没有镜像 $Image, 请先 docker pull / docker load。" }

$cid = 'ace-ow-patch-' + (Get-Random)
$tmp = Join-Path $env:TEMP $cid
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Cleanup {
    if ($cid) { docker rm -f $cid *> $null }
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
trap { Cleanup; throw }

Write-Host "启动临时容器 $cid ..."
docker run -d --name $cid --entrypoint sh $Image -c "sleep 3600" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "临时容器启动失败" }

$root = '/app/build/_app/immutable'
docker exec $cid sh -c "test -d $root" *> $null
if ($LASTEXITCODE -ne 0) { $root = '/app' }
Write-Host "扫描目录 $root ..."

$list = & docker exec $cid sh -c "grep -rl 'Permission denied when accessing microphone' $root 2>/dev/null"
if (-not $list) { Cleanup; Write-Host "未找到目标代码 —— 该镜像的前端结构可能不同, 已中止(未做任何修改)。"; exit 2 }

$patched = 0
foreach ($remote in ($list -split "`n" | Where-Object { $_.Trim() -ne '' })) {
    $remote = $remote.Trim()
    $local  = Join-Path $tmp ([IO.Path]::GetFileName($remote))
    docker cp "${cid}:$remote" $local | Out-Null
    if (Patch-File $local) {
        docker cp $local "${cid}:$remote" | Out-Null
        if ($LASTEXITCODE -eq 0) { $patched++; Write-Host ("[OK] " + $remote) }
    }
}

if ($patched -eq 0) {
    Cleanup
    Write-Host "所有命中文件都未改写(可能已打过补丁, 或代码模式不匹配)。未提交镜像。"
    exit 3
}

Write-Host "提交新镜像 $NewTag ..."
docker commit $cid $NewTag | Out-Null
$ok = ($LASTEXITCODE -eq 0)
Cleanup

if ($ok) {
    Write-Host ""
    Write-Host "=============================================="
    Write-Host " 已生成镜像: $NewTag  (修改 $patched 个前端文件)"
    Write-Host " 下一步: 在 compose\docker-compose.yml 的 open-webui 服务里"
    Write-Host "        把 image 改成: $NewTag"
    Write-Host "        然后: docker compose -f compose\docker-compose.yml up -d --no-build open-webui"
    Write-Host " 离线部署: 记得重新 docker save 这个镜像并更新导出 tar。"
    Write-Host "=============================================="
} else {
    Write-Host "docker commit 失败。"
    exit 4
}
