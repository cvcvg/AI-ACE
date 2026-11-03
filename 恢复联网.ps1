# 恢复联网 —— 只在需要临时联网时用（比如重新准备部署机、排查问题）
# 做四件事：删守卫任务、删出站阻断规则、启用网卡、把相关服务改回手动。
# 用完记得重新跑 第一步-断网.bat，否则这台机器就一直是有网的。

param([switch]$Confirm)

$ErrorActionPreference = "Continue"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[FAIL] 需要管理员权限。请右键这个文件 -> 以管理员身份运行。" -ForegroundColor Red
    Start-Sleep -Seconds 3
    exit 1
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host " 恢复联网：会把这台机器的网卡重新打开" -ForegroundColor Yellow
Write-Host " 恢复后它就能上网了，确认要继续再做。" -ForegroundColor Yellow
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host ""

if (-not $Confirm) {
    Write-Host "    确认要恢复联网，请加 -Confirm 参数（直接双击 恢复联网.bat 即可）。" -ForegroundColor Yellow
    exit 2
}

$report = New-Object System.Collections.ArrayList

# ---------- 1. 先删守卫任务（不删的话 5 分钟内又把网卡关了） ----------
try {
    schtasks /delete /tn "AI-ACE-断网守卫" /f 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        [void]$report.Add("[ OK ] 已删除守卫计划任务")
    } else {
        [void]$report.Add("[ OK ] 没有守卫任务，跳过")
    }
} catch {
    [void]$report.Add("[WARN] 删除守卫任务失败: $($_.Exception.Message)")
}

# ---------- 2. 删出站阻断规则 ----------
try {
    $rules = @(Get-NetFirewallRule -DisplayName "AI-ACE-断网-出站-*" -ErrorAction SilentlyContinue)
    if ($rules.Count -gt 0) {
        $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        [void]$report.Add("[ OK ] 已删除出站阻断规则 $($rules.Count) 条")
    } else {
        [void]$report.Add("[ OK ] 没有出站阻断规则，跳过")
    }
} catch {
    [void]$report.Add("[WARN] 删除防火墙规则失败: $($_.Exception.Message)")
}

# ---------- 3. 启用物理网卡 ----------
$phys = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)
if ($phys.Count -eq 0) {
    [void]$report.Add("[WARN] 没找到物理网卡")
} else {
    foreach ($a in $phys) {
        try {
            Enable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction Stop
            [void]$report.Add("[ OK ] 已启用网卡: $($a.Name)")
        } catch {
            [void]$report.Add("[FAIL] 启用网卡失败: $($a.Name) -> $($_.Exception.Message)")
        }
    }
}

# ---------- 4. 服务改回手动 ----------
foreach ($svc in @("SharedAccess", "DiagTrack")) {
    try {
        Set-Service -Name $svc -StartupType Manual -ErrorAction Stop
        [void]$report.Add("[ OK ] 服务已改回手动: $svc")
    } catch {
        [void]$report.Add("[WARN] 服务处理失败: $svc -> $($_.Exception.Message)")
    }
}

# ---------- 5. 等网络起来再验证 ----------
Write-Host "    等 8 秒让网络重新连上..." -ForegroundColor DarkGray
Start-Sleep -Seconds 8

$ok = $false
try {
    $c = New-Object System.Net.Sockets.TcpClient
    $t = $c.BeginConnect("223.5.5.5", 443, $null, $null)
    if ($t.AsyncWaitHandle.WaitOne(3000)) { $c.EndConnect($t); $ok = $true }
    $c.Close()
} catch { $ok = $false }

if ($ok) {
    [void]$report.Add("[ OK ] 验证：可以访问外网")
} else {
    [void]$report.Add("[WARN] 验证：还连不上外网，可能是网线没插 / WiFi 没连，手工连一下再试")
}

Write-Host ""
foreach ($line in $report) {
    if ($line -like "*[FAIL]*") { Write-Host $line -ForegroundColor Red }
    elseif ($line -like "*[WARN]*") { Write-Host $line -ForegroundColor Yellow }
    else { Write-Host $line -ForegroundColor Green }
}
Write-Host ""
Write-Host "完成后如果还要继续当部署机用，请重新运行 第一步-断网.bat。" -ForegroundColor Cyan
Write-Host "按任意键关闭..." -ForegroundColor DarkGray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
