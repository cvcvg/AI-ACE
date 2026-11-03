param([switch]$Confirm, [switch]$Restore)
$ErrorActionPreference = "Stop"
$Tag = "AI-ACE-白名单-"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "[FAIL] 需要管理员权限, 请右键以管理员身份运行."; exit 1 }

if ($Restore) {
    Get-NetFirewallRule -DisplayName "$Tag*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Set-NetFirewallProfile -Profile Domain,Private,Public -DefaultOutboundAction Allow
    Write-Host "[ OK ] 已恢复: 出站默认允许, 白名单规则已删除."
    exit 0
}

if (-not $Confirm) {
    Write-Host "本脚本把 Windows 防火墙的出站默认策略改成 [阻止], 只放行两样:"
    Write-Host "  1) 本机回环 127.0.0.1 / ::1"
    Write-Host "  2) Docker / WSL 的虚拟网卡 (vEthernet*)"
    Write-Host ""
    Write-Host "效果: 就算插了网线、连了 WiFi, 数据也发不出去;"
    Write-Host "      AI-ACE 全部服务走 127.0.0.1, 不受影响."
    Write-Host ""
    Write-Host "确认执行请加 -Confirm; 想撤销请加 -Restore."
    exit 2
}

# 1. 记录原始状态
$snap = Join-Path $PSScriptRoot "防火墙-原始状态.txt"
"记录时间: $(Get-Date -Format s)" | Set-Content -Path $snap -Encoding UTF8
Get-NetFirewallProfile | ForEach-Object { "$($_.Name) DefaultOutboundAction=$($_.DefaultOutboundAction)" } | Add-Content -Path $snap -Encoding UTF8

# 2. 清掉旧规则
Get-NetFirewallRule -DisplayName "$Tag*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

# 3. 白名单: 回环 + 虚拟网卡
New-NetFirewallRule -DisplayName "${Tag}环回" -Direction Outbound -Action Allow -Profile Any -RemoteAddress @("127.0.0.1","::1") -ErrorAction SilentlyContinue | Out-Null
$vifs = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -match "Hyper-V|WSL|Docker" -and $_.Status -eq "Up" })
foreach ($a in $vifs) {
    New-NetFirewallRule -DisplayName "${Tag}虚拟网卡-$($a.Name)" -Direction Outbound -Action Allow -Profile Any -InterfaceAlias $a.Name -ErrorAction SilentlyContinue | Out-Null
}
Write-Host ("[ OK ] 白名单规则: 回环 1 条, 虚拟网卡 {0} 条" -f $vifs.Count)
if ($vifs.Count -eq 0) { Write-Host "[warn] 没发现运行中的 Docker/WSL 虚拟网卡; 要用 Docker 的话, 先启动 Docker Desktop 再重跑本脚本." }

# 4. 出站默认阻止
Set-NetFirewallProfile -Profile Domain,Private,Public -DefaultOutboundAction Block
Write-Host "[ OK ] 出站默认策略 = Block"

# 5. 验证
$profiles = @(Get-NetFirewallProfile | Where-Object { $_.DefaultOutboundAction -ne "Block" })
if ($profiles.Count -gt 0) { Write-Host "[FAIL] 有配置文件未生效"; exit 3 }

$blocked = $false
try {
    $c = New-Object System.Net.Sockets.TcpClient
    $t = $c.BeginConnect("1.1.1.1", 443, $null, $null)
    if ($t.AsyncWaitHandle.WaitOne(3000)) { $blocked = $false; $c.EndConnect($t) } else { $blocked = $true }
    $c.Close()
} catch { $blocked = $true }

if ($blocked) {
    Write-Host "[ OK ] 外网 TCP 已被丢弃 (1.1.1.1:443 不可达)"
    Write-Host ""
    Write-Host "结果: 已启用出站白名单. 撤销请运行: 防火墙-只走内网.bat -Restore"
    exit 0
} else {
    Write-Host "[WARN] 仍能连上 1.1.1.1:443, 请检查是否有第三方防火墙/代理接管"
    exit 3
}
