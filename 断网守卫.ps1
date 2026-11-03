# 断网守卫 —— 由计划任务每 5 分钟跑一次
# 作用：任何"新出现"的物理网卡（比如有人插了 USB 网卡/手机共享）都会被立刻禁用并加出站阻断规则。
# 说明：只处理物理网卡，不动 vEthernet（WSL/Docker）。
$ErrorActionPreference = "SilentlyContinue"
$logDir = $PSScriptRoot
$log = Join-Path $logDir "断网-守卫.log"

$phys = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq "Up" })
if ($phys.Count -eq 0) { exit 0 }

$lines = @()
foreach ($a in $phys) {
    $ruleName = "AI-ACE-断网-出站-$($a.Name)"
    # 顺序很重要：网卡还是 Up 的时候才能按接口名建规则；禁用之后就找不到这个接口了
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Action Block -Profile Any -InterfaceAlias $a.Name -Enabled True | Out-Null
    Disable-NetAdapter -Name $a.Name -Confirm:$false
    $lines += "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') 已阻断: $($a.Name)"
}
$lines | Out-File -FilePath $log -Append -Encoding UTF8
exit 0
