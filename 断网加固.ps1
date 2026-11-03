# 断网加固 v2 —— 永久离线（AI-ACE 部署机专用）
# 用法：以管理员身份运行
#   powershell -ExecutionPolicy Bypass -File 断网加固.ps1 -Confirm
# 做什么：
#   1) 禁用全部物理网卡（以太网 / 无线），不碰 vEthernet（WSL/Docker 虚拟网卡）
#   2) 给每张物理网卡加"出站阻断"防火墙规则：即使有人手动启用网卡，也发不出去
#   3) 关掉移动热点服务（SharedAccess），防止用手机共享网络绕过
#   4) 关掉诊断遥测（DiagTrack）与 OneDrive 同步、跨设备剪贴板
# 设计说明：本脚本**没有恢复开关**。要恢复只能由管理员在 Windows 设置里手工启用网卡并删除防火墙规则。
param(
    [switch]$Confirm
)

$ErrorActionPreference = "Continue"
$record = Join-Path $PSScriptRoot "断网-记录.txt"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host "[X] 需要管理员权限：右键 断网.bat -> 以管理员身份运行。" -ForegroundColor Red
    exit 1
}

if (-not $Confirm) {
    Write-Host "[!] 这个脚本会让本机永久离线，没有一键恢复。" -ForegroundColor Yellow
    Write-Host "    确认要断网请加 -Confirm 参数（直接双击 断网.bat 即可）。" -ForegroundColor Yellow
    exit 2
}

$report = New-Object System.Collections.ArrayList

# ---------- 1. 取物理网卡 ----------
$phys = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue)
if ($phys.Count -eq 0) {
    [void]$report.Add("[WARN] 没找到物理网卡，可能已经是断开状态")
}

# ---------- 2. 出站防火墙阻断（必须在禁用网卡之前建，禁用后接口就不存在了） ----------
foreach ($a in $phys) {
    $ruleName = "AI-ACE-断网-出站-$($a.Name)"
    try {
        Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Action Block -Profile Any `
            -InterfaceAlias $a.Name -Enabled True -ErrorAction Stop | Out-Null
        [void]$report.Add("[ OK ] 出站阻断规则: $ruleName")
    } catch {
        [void]$report.Add("[FAIL] 防火墙规则失败: $ruleName -> $($_.Exception.Message)")
    }
}

# ---------- 3. 禁用物理网卡（主防线） ----------
if ($phys.Count -gt 0) {
    $names = @()
    foreach ($a in $phys) {
        $names += $a.Name
        try {
            Disable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction Stop
            [void]$report.Add("[ OK ] 已禁用网卡: $($a.Name) ($($a.InterfaceDescription))")
        } catch {
            [void]$report.Add("[FAIL] 禁用网卡失败: $($a.Name) -> $($_.Exception.Message)")
        }
    }
    Set-Content -Path $record -Value $names -Encoding UTF8
    [void]$report.Add("[ OK ] 网卡名已记录到: $record")
}

# ---------- 4. 移动热点 ----------
try {
    Set-Service -Name SharedAccess -StartupType Disabled -ErrorAction Stop
    Stop-Service -Name SharedAccess -Force -ErrorAction SilentlyContinue
    [void]$report.Add("[ OK ] 移动热点服务已关闭")
} catch {
    [void]$report.Add("[WARN] 移动热点服务处理失败: $($_.Exception.Message)")
}

# ---------- 5. 遥测 / OneDrive / 跨设备剪贴板 ----------
try {
    Set-Service -Name DiagTrack -StartupType Disabled -ErrorAction Stop
    Stop-Service -Name DiagTrack -Force -ErrorAction SilentlyContinue
    New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Force | Out-Null
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection" -Name AllowTelemetry -Value 0 -Type DWord
    [void]$report.Add("[ OK ] 诊断遥测已关闭")
} catch {
    [void]$report.Add("[WARN] 遥测关闭失败: $($_.Exception.Message)")
}

try {
    Get-Process OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive" -Force | Out-Null
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive" -Name DisableFileSyncNGSC -Value 1 -Type DWord
    [void]$report.Add("[ OK ] OneDrive 同步已禁用")
} catch {
    [void]$report.Add("[WARN] OneDrive 处理失败: $($_.Exception.Message)")
}

try {
    New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Force | Out-Null
    Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\System" -Name AllowCrossDeviceClipboard -Value 0 -Type DWord
    [void]$report.Add("[ OK ] 跨设备剪贴板已关闭")
} catch {
    [void]$report.Add("[WARN] 剪贴板策略失败: $($_.Exception.Message)")
}

# ---------- 6. 守卫计划任务（每 5 分钟兜底） ----------
try {
    $guard = Join-Path $PSScriptRoot "断网守卫.ps1"
    if (Test-Path $guard) {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" `
            -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$guard`""
        $t1 = New-ScheduledTaskTrigger -AtStartup
        $t2 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
            -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
        Register-ScheduledTask -TaskName "AI-ACE-断网守卫" -Action $action -Trigger @($t1, $t2) `
            -Settings $settings -RunLevel Highest -User "SYSTEM" -Force | Out-Null
        [void]$report.Add("[ OK ] 已注册守卫任务: 每 5 分钟检查一次新网卡")
    } else {
        [void]$report.Add("[WARN] 没找到 断网守卫.ps1，跳过定时兜底")
    }
} catch {
    [void]$report.Add("[WARN] 注册守卫任务失败: $($_.Exception.Message)")
}

# ---------- 7. 验证 ----------
$stillUp = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq "Up" })
if ($stillUp.Count -eq 0) {
    [void]$report.Add("[ OK ] 验证: 没有处于启用状态的物理网卡")
} else {
    [void]$report.Add("[FAIL] 验证: 仍有网卡在运行 -> " + ($stillUp.Name -join ", "))
}

$blocked = 0
foreach ($ip in @("1.1.1.1", "223.5.5.5")) {
    $r = Test-NetConnection -ComputerName $ip -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue
    if ($r) {
        [void]$report.Add("[FAIL] 验证: 仍能连到 $ip:443")
    } else {
        $blocked++
        [void]$report.Add("[ OK ] 验证: $ip:443 不可达")
    }
}

$report | ForEach-Object { Write-Host $_ }
Write-Host ""
if ($blocked -eq 2 -and $stillUp.Count -eq 0) {
    Write-Host "结果: 已断网。本机除 127.0.0.1 以外没有任何网络出口。" -ForegroundColor Green
    exit 0
} else {
    Write-Host "结果: 仍有出口，请把上面的 [FAIL] 截图给管理员。" -ForegroundColor Red
    exit 3
}
