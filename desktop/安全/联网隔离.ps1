<#
  AI-ACE 联网隔离：拦住容器出网，并且能自证。
  改的是 docker-desktop 这个 WSL 发行版里的 iptables（不是 Windows 防火墙）：
      DOCKER-USER --jump--> AIACE_EGRESS
        1-5  -d 127.0.0.0/8 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16 169.254.0.0/16 -j RETURN
        6    -j LOG --log-prefix AIACE-BLOCK
        7    -j DROP
  先 RETURN 私网段，让容器之间互访照常（容器互访同样经过 DOCKER-USER）；Docker Desktop 重启后规则就没了，要重跑 -Apply。
  用法：powershell -ExecutionPolicy Bypass -File 联网隔离.ps1 [-Apply|-Remove|-Report] [-DryRun] [-Quiet] [-ReportPath <文件>]
    无参     只审计容器网络 + 现有规则，不改任何东西，报告照写
    -Report  完整自检：多查宿主监听口、做外连试探
    -Apply   装拦截规则，并关掉 Docker Desktop 里已有的遥测/更新开关
    -Remove  回滚：删 DOCKER-USER 里的跳转和 AIACE_EGRESS 链
    -DryRun  只打印计划，一条命令都不执行
  退出码：0 全绿 / 1 有风险项 / 2 执行失败；最后一行固定 RESULT: OK|WARN|FAIL <一句话>
#>
[CmdletBinding()]
param([switch]$Apply, [switch]$Report, [switch]$Remove, [switch]$Quiet, [switch]$DryRun, [string]$ReportPath)

$ErrorActionPreference = 'Continue'
$script:ChainName = 'AIACE_EGRESS'; $script:ParentChain = 'DOCKER-USER'; $script:WslDistro = 'docker-desktop'
$script:PrivateNets = @('127.0.0.0/8', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '169.254.0.0/16')
$script:Ports       = @(3000, 9000, 8010, 8011, 8081)
$script:CRLF = [string][char]13 + [string][char]10; $script:QuietMode = [bool]$Quiet; $script:Lines = New-Object System.Collections.ArrayList
$script:Risk = 0; $script:Hard = 0; $script:Unknown = 0
$script:EngineUp = $false; $script:WslOk = $false; $script:IptablesOk = $false
$script:WriteReport = -not [bool]$DryRun
$script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:WslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $script:WslExe)) { $script:WslExe = 'wsl.exe' }
function Write-Line {
    param([string]$Text, [string]$Color)
    [void]$script:Lines.Add($Text)
    if ($script:QuietMode) { return }
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}
function Write-Section { param([string]$t) Write-Line ''; Write-Line ('== ' + $t + ' ==') 'Cyan' }
function Add-Ok { param([string]$t) Write-Line ('[通过] ' + $t) 'Green' }
function Add-Unknown { param([string]$t) $script:Unknown++; Write-Line ('[无法确认] ' + $t) 'DarkYellow' }
function Add-Hard { param([string]$t) $script:Hard++; Write-Line ('[失败] ' + $t) 'Red' }
function Add-Risk {
    param([string]$t, [switch]$Loud)
    $script:Risk++
    if (-not $Loud) { Write-Line ('[风险] ' + $t) 'Yellow'; return }
    $msg = '[风险] ' + $t
    [void]$script:Lines.Add($msg)
    if (-not $script:QuietMode) { Write-Host ('!!! ' + $msg + ' !!!') -ForegroundColor White -BackgroundColor DarkRed }
}
function Clear-Text {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    if ($Text.IndexOf([char]0) -ge 0) { $Text = $Text.Replace([string][char]0, '') }   # wsl.exe 输出是 UTF-16LE
    return $Text.Trim()
}
function Invoke-Exe {
    param([string]$FilePath, [string[]]$ArgList)
    $quoted = @($ArgList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = ($quoted -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $result = @{ Code = 127; Out = ''; Err = '' }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    try {
        [void]$p.Start()
        $errTask = $p.StandardError.ReadToEndAsync()
        $outText = $p.StandardOutput.ReadToEnd()
        $errText = $errTask.Result
        [void]$p.WaitForExit()
        $result.Code = $p.ExitCode
        $result.Out = Clear-Text $outText
        $result.Err = Clear-Text $errText
    } catch {
        $result.Code = 127
        $result.Err = $_.Exception.Message
    } finally { $p.Dispose() }
    return $result
}
function Invoke-Wsl {
    param([string[]]$CmdArgs)
    return (Invoke-Exe -FilePath $script:WslExe -ArgList (@('-d', $script:WslDistro, '-u', 'root', '--') + $CmdArgs))
}
function Invoke-Docker {
    param([string[]]$CmdArgs)
    $cmd = Get-Command 'docker.exe' -ErrorAction SilentlyContinue
    if (-not $cmd) { return @{ Code = 127; Out = ''; Err = 'docker.exe 不在 PATH 里' } }
    return (Invoke-Exe -FilePath $cmd.Source -ArgList $CmdArgs)
}
function Test-Wsl {
    $r = Invoke-Exe -FilePath $script:WslExe -ArgList @('-l', '-q')
    if ($r.Code -ne 0) { return $false }
    return (($r.Out + ' ' + $r.Err) -match [regex]::Escape($script:WslDistro))
}
function Test-Engine {
    $cmd = Get-Command 'docker.exe' -ErrorAction SilentlyContinue
    if (-not $cmd) { return $false }
    $r = Invoke-Exe -FilePath $cmd.Source -ArgList @('info', '--format', '{{.ServerVersion}}')
    return ($r.Code -eq 0 -and $r.Out.Trim().Length -gt 0)
}
function Get-ReportFile {
    if ($ReportPath) {
        if ([System.IO.Path]::IsPathRooted($ReportPath)) { return $ReportPath }
        return (Join-Path $script:ScriptDir $ReportPath)
    }
    return (Join-Path $script:ScriptDir ('报告\联网隔离-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt'))
}
function Complete-Run {
    param([string]$Verdict, [string]$Sentence)
    $color = 'Green'; if ($Verdict -eq 'WARN') { $color = 'Yellow' }; if ($Verdict -eq 'FAIL') { $color = 'Red' }
    $line = 'RESULT: ' + $Verdict + ' ' + $Sentence
    if ($script:WriteReport) {
        $path = Get-ReportFile
        Write-Line ('报告: ' + $path) 'Cyan'
        [void]$script:Lines.Add($line)
        try {
            $parent = Split-Path -Parent $path
            if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            $text = ($script:Lines -join $script:CRLF) + $script:CRLF
            [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
        } catch {
            Write-Host ('报告写不进去: ' + $_.Exception.Message) -ForegroundColor Red
            $line = 'RESULT: FAIL 报告写不进去：' + $_.Exception.Message
            $color = 'Red'
            $Verdict = 'FAIL'
        }
    } else { [void]$script:Lines.Add($line) }
    Write-Host $line -ForegroundColor $color
    if ($Verdict -eq 'FAIL') { exit 2 }; if ($Verdict -eq 'WARN') { exit 1 }; exit 0
}
function Show-Containers {
    Write-Section '1/4 容器网络与端口绑定 (docker ps)'
    if (-not $script:EngineUp) { Add-Unknown 'Docker 引擎没在跑，容器网络无法核对（先启动 Docker Desktop 再跑一次）'; return }
    $r = Invoke-Docker @('ps', '--format', '{{.Names}}|{{.Networks}}|{{.Ports}}')
    if ($r.Code -ne 0) { Add-Unknown ('docker ps 没跑成功: ' + $r.Err); return }
    $rows = @()
    foreach ($line in ($r.Out -split '\r?\n')) {
        if ($line -notmatch 'ace|open-webui') { continue }
        $parts = $line -split '\|'
        if ($parts.Count -lt 3) { continue }
        $rows += , @($parts[0], $parts[1], $parts[2])
    }
    if ($rows.Count -eq 0) { Add-Risk '没看到 AI-ACE 的容器（服务没起？）'; return }
    Write-Line ('看到 ' + $rows.Count + ' 个容器（compose 里共 13 个）')
    foreach ($row in $rows) {
        $name = $row[0]; $nets = $row[1]; $ports = $row[2]
        $showPorts = $ports; if (-not $showPorts) { $showPorts = '(无)' }
        Write-Line ('  ' + $name.PadRight(24) + ' 网络=' + $nets + '  端口=' + $showPorts)
        if ($name -ne 'ace-gateway') {
            if ($ports) { Add-Risk ($name + ' 把端口发布到宿主机了（应该只有 ace-gateway 发布）') }
            if ($nets -match 'ai-edge-net') { Add-Risk ($name + ' 也挂在 ai-edge-net 上（只有 ace-gateway 该挂）') }
            if ($nets -notmatch 'ai-private-net') { Add-Risk ($name + ' 不在 ai-private-net 里') }
        } else {
            if ($nets -notmatch 'ai-edge-net') { Add-Risk 'ace-gateway 没挂 ai-edge-net（宿主机就访问不到界面）' }
            if (-not $ports) { Add-Risk 'ace-gateway 没有发布端口（3000/9000 等映射不出去）' }
            foreach ($bind in ($ports -split ',\s*')) {
                if ($bind -and $bind -notmatch '^127\.0\.0\.1:') { Add-Risk ('ace-gateway 的绑定不是回环地址: ' + $bind) }
            }
        }
    }
}
function Show-Listeners {
    Write-Section '2/4 宿主机监听地址 (Get-NetTCPConnection -State Listen)'
    try { $conns = @(Get-NetTCPConnection -State Listen -ErrorAction Stop) }
    catch { Add-Unknown ('读不到监听表: ' + $_.Exception.Message); return }
    $found = 0
    foreach ($port in $script:Ports) {
        $hits = @($conns | Where-Object { $_.LocalPort -eq $port })
        if ($hits.Count -eq 0) { Write-Line ('  ' + $port + '  无监听（服务没起时正常）'); continue }
        $found++
        foreach ($h in $hits) {
            $procName = '?'; try { $procName = (Get-Process -Id $h.OwningProcess -ErrorAction Stop).ProcessName } catch { }
            Write-Line ('  ' + $port + '  LocalAddress=' + $h.LocalAddress + '  pid=' + $h.OwningProcess + ' ' + $procName)
            if ($h.LocalAddress -ne '127.0.0.1' -and $h.LocalAddress -ne '::1') {
                Add-Risk ('' + $port + ' 监听在 ' + $h.LocalAddress + '，局域网里别的机器可能连得上')
            }
        }
    }
    if ($found -eq 0) { Write-Line '  五个端口都没人监听（Docker 没启动时就是这样）' }
}
function Show-Iptables {
    Write-Section '3/4 拦截规则原文 (docker-desktop WSL 里的 iptables)'
    if (-not $script:WslOk) { Add-Unknown '连不上 docker-desktop 这个 WSL 发行版（Docker Desktop 没装或 WSL 不可用），规则无法核对'; return }
    if (-not $script:IptablesOk) { Add-Unknown '这个发行版里没有 iptables（引擎没启动时就是这样，退出码 127），规则无法核对'; return }
    $r = Invoke-Wsl @('-S', $script:ParentChain)
    if ($r.Code -ne 0) {
        Add-Unknown ('DOCKER-USER 链读不到（引擎没起来时就没有这条链）: ' + $r.Err)
    } else {
        Write-Line '--- iptables -S DOCKER-USER ---'
        foreach ($l in ($r.Out -split '\r?\n')) { if ($l) { Write-Line ('  ' + $l) } }
        if ($r.Out -notmatch $script:ChainName) {
            Add-Risk ('DOCKER-USER 里没有指向 ' + $script:ChainName + ' 的跳转，拦截没装上或已失效')
        } else { Add-Ok ('DOCKER-USER 里有指向 ' + $script:ChainName + ' 的跳转') }
    }
    $c = Invoke-Wsl @('-S', $script:ChainName)
    if ($c.Code -ne 0) { Write-Line ('链 ' + $script:ChainName + ': 不存在（未装拦截）'); return }
    Write-Line ('链 ' + $script:ChainName + ': 已存在')
    $v = Invoke-Wsl @('-L', $script:ChainName, '-n', '-v')
    if ($v.Code -ne 0) { Add-Unknown ('读计数器失败: ' + $v.Err); return }
    Write-Line '--- iptables -L AIACE_EGRESS -n -v ---'
    foreach ($l in ($v.Out -split '\r?\n')) { if ($l) { Write-Line ('  ' + $l) } }
    $drop = 0
    foreach ($l in ($v.Out -split '\r?\n')) { if ($l -match 'DROP' -and $l -match '^\s*(\d+)') { $drop = [int]$Matches[1] } }
    Write-Line ('DROP 规则命中计数: ' + $drop + ' 个包')
    if ($drop -gt 0) { Write-Line '  计数在涨 = 确实拦下过容器发往公网的包（0 也正常：还没人试过）' }
    if ($v.Out -notmatch 'DROP') { Add-Risk ('链 ' + $script:ChainName + ' 里没有 DROP 规则，出网没被拦') }
}
function Show-Egress {
    Write-Section '4/4 端到端外连试探 (ace-gateway 里连 1.1.1.1:80)'
    if (-not $script:EngineUp) { Add-Unknown 'Docker 引擎没在跑，外连试探做不了'; return }
    $names = Invoke-Docker @('ps', '--format', '{{.Names}}')
    if ($names.Out -notmatch '(?m)^ace-gateway\r?$') { Add-Unknown 'ace-gateway 没在运行，外连试探做不了'; return }
    $code = "import socket;print('PROBE-START');socket.create_connection(('1.1.1.1',80),3);print('CONNECTED')"
    $r = Invoke-Docker @('exec', 'ace-gateway', 'python', '-c', $code)
    if ($r.Out -match 'CONNECTED') { Add-Risk '成功连上 1.1.1.1:80 —— 容器还能出网，拦截没生效' -Loud; return }
    if ($r.Out -match 'PROBE-START') {
        Add-Ok '连不上 1.1.1.1:80（这才是对的）'
        $last = @($r.Err -split '\r?\n' | Where-Object { $_ }) | Select-Object -Last 1
        if ($last) { Write-Line ('  容器里的报错: ' + $last) }
        return
    }
    Add-Unknown ('试探没跑起来（容器里没有 python，或 docker exec 失败）: ' + $r.Err)
}
function Get-SettingsPath {
    $base = $env:APPDATA; if (-not $base) { $base = [Environment]::GetFolderPath('ApplicationData') }
    if (-not $base) { $base = Join-Path $env:USERPROFILE 'AppData\Roaming' }
    if (-not $base) { return '' }
    return (Join-Path $base 'Docker\settings-store.json')
}
function Get-TelemetryKeys {
    param($Obj)
    $keys = @()
    foreach ($p in $Obj.PSObject.Properties) {
        if ($p.Name -match 'Telemetry|Update|Analytics' -and $p.Value -is [bool]) { $keys += $p.Name }
    }
    return $keys
}

function Show-Telemetry {
    Write-Section '附 Docker Desktop 遥测/更新开关 (settings-store.json)'
    $path = Get-SettingsPath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { Add-Unknown ('没找到 ' + $path); return }
    try { $obj = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { Add-Unknown ('读不了或不是合法 JSON: ' + $_.Exception.Message); return }
    $keys = @(Get-TelemetryKeys $obj)
    if ($keys.Count -eq 0) { Write-Line '  你的 Docker 版本没有这些键，请在 Settings → General 里手动关掉使用统计和自动更新检查。'; return }
    foreach ($k in $keys) { Write-Line ('  ' + $k + ' = ' + $obj.PSObject.Properties[$k].Value) }
    $on = @($keys | Where-Object { $obj.PSObject.Properties[$_].Value -eq $true })
    if ($on.Count -gt 0) { Add-Risk ('这些开关还开着: ' + ($on -join ', ') + '（用 -Apply 关掉）') }
}
function Set-Telemetry {
    Write-Section '把 Docker Desktop 已有的遥测/更新开关关掉'
    $path = Get-SettingsPath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { Add-Unknown ('没找到 ' + $path); return }
    try { $obj = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { Add-Unknown ('读不了或不是合法 JSON: ' + $_.Exception.Message); return }
    $keys = @(Get-TelemetryKeys $obj)
    if ($keys.Count -eq 0) {
        Write-Line '  你的 Docker 版本没有这些键，请在 Settings → General 里手动关掉使用统计和自动更新检查。'
        Write-Line '  （本脚本不凭空新增键，避免把 Docker 的设置文件写坏）'
        return
    }
    foreach ($k in $keys) { Write-Line ('  发现 ' + $k + ' = ' + $obj.PSObject.Properties[$k].Value) }
    $on = @($keys | Where-Object { $obj.PSObject.Properties[$_].Value -eq $true })
    if ($on.Count -eq 0) { Write-Line '  这些开关本来就关着，不用改'; return }
    $backup = $path + '.ai-ace-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    try { Copy-Item -LiteralPath $path -Destination $backup -Force }
    catch { Add-Risk ('备份失败，没敢改: ' + $_.Exception.Message); return }
    foreach ($k in $on) { $obj.PSObject.Properties[$k].Value = $false }
    try {
        $json = $obj | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
        Add-Ok ('已关掉 ' + ($on -join ', ') + '；原文件备份在 ' + $backup)
    } catch {
        Add-Risk ('写回失败: ' + $_.Exception.Message + '（备份还在 ' + $backup + '，可手动还原）')
    }
}
function Show-Plan {
    Write-Section '计划（-DryRun：下面这些一条都没执行）'
    $w = 'wsl -d ' + $script:WslDistro + ' -u root -- '
    if ($Mode -eq 'Apply') {
        Write-Line ('  ' + $w + 'iptables -N ' + $script:ChainName + '        # 已存在则忽略')
        Write-Line ('  ' + $w + 'iptables -F ' + $script:ChainName)
        foreach ($net in $script:PrivateNets) { Write-Line ('  ' + $w + 'iptables -A ' + $script:ChainName + ' -d ' + $net + ' -j RETURN') }
        Write-Line ('  ' + $w + 'iptables -A ' + $script:ChainName + ' -j LOG --log-prefix AIACE-BLOCK')
        Write-Line ('  ' + $w + 'iptables -A ' + $script:ChainName + ' -j DROP')
        Write-Line ('  ' + $w + 'iptables -C DOCKER-USER -j ' + $script:ChainName + '  （失败才 insert）')
        Write-Line ('  ' + $w + 'iptables -I DOCKER-USER 1 -j ' + $script:ChainName)
        Write-Line '  备份 settings-store.json 后，把已有的 Telemetry/Update/Analytics 布尔键改成 false'
    } elseif ($Mode -eq 'Remove') {
        Write-Line ('  ' + $w + 'iptables -D DOCKER-USER -j ' + $script:ChainName + '   # 有几条删几条')
        Write-Line ('  ' + $w + 'iptables -F ' + $script:ChainName)
        Write-Line ('  ' + $w + 'iptables -X ' + $script:ChainName)
    } else {
        Write-Line '  docker ps  （只看 AI-ACE 容器的网络与端口绑定）'
        Write-Line ('  ' + $w + 'iptables -S DOCKER-USER')
        if ($Report) {
            Write-Line '  Get-NetTCPConnection -State Listen  （3000/9000/8010/8011/8081）'
            Write-Line '  docker exec ace-gateway python -c "...create_connection(1.1.1.1,80)..."'
            Write-Line ('  读 ' + (Get-SettingsPath) + ' 里的遥测开关（只读）')
        }
        Write-Line ('  报告写到 ' + (Get-ReportFile))
    }
}
function Invoke-Apply {
    Write-Section '装拦截规则 AIACE_EGRESS'
    if (-not $script:WslOk) { Add-Hard 'wsl 不可用，或没有 docker-desktop 这个发行版，装不了'; return }
    if (-not $script:IptablesOk) { Add-Hard 'docker-desktop 这个 WSL 里没有 iptables（引擎没启动）：先启动 Docker Desktop 等它 Running，再跑 -Apply'; return }
    $r = Invoke-Wsl @('-S', $script:ParentChain)
    if ($r.Code -ne 0) { Add-Hard ('DOCKER-USER 链不存在，先把 Docker Desktop 启动起来: ' + $r.Err); return }

    $r = Invoke-Wsl @('-N', $script:ChainName)
    if ($r.Code -ne 0) {
        if ((Invoke-Wsl @('-S', $script:ChainName)).Code -ne 0) { Add-Hard ('建链失败: ' + $r.Err); return }
        Write-Line ('链 ' + $script:ChainName + ' 已存在，沿用')
    } else { Write-Line ('已建链 ' + $script:ChainName) }
    $r = Invoke-Wsl @('-F', $script:ChainName)
    if ($r.Code -ne 0) { Add-Hard ('清空链失败: ' + $r.Err); return }
    foreach ($net in $script:PrivateNets) {
        $r = Invoke-Wsl @('-A', $script:ChainName, '-d', $net, '-j', 'RETURN')
        if ($r.Code -ne 0) { Add-Hard ('加 RETURN ' + $net + ' 失败: ' + $r.Err); return }
        Write-Line ('  RETURN ' + $net)
    }
    $r = Invoke-Wsl @('-A', $script:ChainName, '-j', 'LOG', '--log-prefix', 'AIACE-BLOCK')
    if ($r.Code -ne 0) { Add-Hard ('加 LOG 失败: ' + $r.Err); return }
    Write-Line '  LOG --log-prefix AIACE-BLOCK'
    $r = Invoke-Wsl @('-A', $script:ChainName, '-j', 'DROP')
    if ($r.Code -ne 0) { Add-Hard ('加 DROP 失败: ' + $r.Err); return }
    Write-Line '  DROP'

    if ((Invoke-Wsl @('-C', $script:ParentChain, '-j', $script:ChainName)).Code -eq 0) {
        Write-Line 'DOCKER-USER 里已有跳转，没重复加'
    } else {
        $r = Invoke-Wsl @('-I', $script:ParentChain, '1', '-j', $script:ChainName)
        if ($r.Code -ne 0) { Add-Hard ('插入跳转失败: ' + $r.Err); return }
        Write-Line '已在 DOCKER-USER 第 1 条插入跳转'
    }
    Add-Ok '拦截规则已就位'
    Set-Telemetry
}
function Invoke-Remove {
    Write-Section '回滚拦截规则'
    if (-not $script:WslOk) { Add-Hard 'wsl 不可用，或没有 docker-desktop 这个发行版，回滚不了'; return }
    if (-not $script:IptablesOk) { Add-Hard 'docker-desktop 这个 WSL 里没有 iptables（引擎没启动），回滚不了'; return }
    $n = 0
    while ($n -lt 5) {
        if ((Invoke-Wsl @('-C', $script:ParentChain, '-j', $script:ChainName)).Code -ne 0) { break }
        $d = Invoke-Wsl @('-D', $script:ParentChain, '-j', $script:ChainName)
        if ($d.Code -ne 0) { Add-Risk ('删跳转失败: ' + $d.Err); break }
        $n++
        Write-Line '已删掉 DOCKER-USER 里的一条跳转'
    }
    if ($n -eq 0) { Write-Line 'DOCKER-USER 里本来就没有跳转' }
    $r = Invoke-Wsl @('-F', $script:ChainName)
    if ($r.Code -ne 0) { Write-Line ('  链不存在，或清空失败: ' + $r.Err) }
    $r = Invoke-Wsl @('-X', $script:ChainName)
    if ($r.Code -ne 0) { Add-Risk ('删链失败（可能还有别的规则引用它）: ' + $r.Err) }
    else { Write-Line ('已删链 ' + $script:ChainName) }
    Add-Ok '回滚完成，容器恢复原来的出网能力（Docker Desktop 重启后也会自动没）'
}

Write-Line ('AI-ACE 联网隔离  ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $PSCommandPath)

$Mode = 'Audit'
if ($Remove) { $Mode = 'Remove' } elseif ($Apply) { $Mode = 'Apply' }
if ($Apply -and $Remove) {
    Write-Host '-Apply 和 -Remove 不能一起用' -ForegroundColor Red
    Write-Host 'RESULT: FAIL 参数冲突：-Apply 与 -Remove 只能选一个'
    exit 2
}
if ($DryRun) {
    Show-Plan
    Write-Line '（-DryRun 没有改动任何规则，也没有写报告）'
    Complete-Run 'OK' '计划已打印，未执行任何命令'
}

$script:WslOk = Test-Wsl
$script:EngineUp = Test-Engine
if ($script:WslOk) { $script:IptablesOk = ((Invoke-Wsl @('sh', '-c', 'command -v iptables')).Code -eq 0) }
if (-not $script:EngineUp) { Write-Line '提示: Docker 引擎没在跑（或者 docker.exe 不在 PATH 里）' }
Write-Line ('环境: wsl=' + $script:WslOk + '  engine=' + $script:EngineUp + '  iptables=' + $script:IptablesOk)

if ($Mode -eq 'Apply') {
    Invoke-Apply
} elseif ($Mode -eq 'Remove') {
    Invoke-Remove
} elseif ($Report) {
    Show-Containers; Show-Listeners; Show-Iptables; Show-Egress; Show-Telemetry
} else {
    Show-Containers; Show-Iptables
}

if ($script:Hard -gt 0) { Complete-Run 'FAIL' ('有 ' + $script:Hard + ' 处执行失败，按上面的 [失败] 行处理后重跑') }
if (($script:Risk + $script:Unknown) -gt 0) { Complete-Run 'WARN' ('有 ' + $script:Risk + ' 个风险项、' + $script:Unknown + ' 项无法确认，详见报告') }
if ($Mode -eq 'Apply') { Complete-Run 'OK' '拦截已就位：容器出网被 DROP；Docker Desktop 重启后要重跑 -Apply' }
if ($Mode -eq 'Remove') { Complete-Run 'OK' '已回滚：跳转和链都删了' }
Complete-Run 'OK' '边界核对通过：私有网段直连、只有网关发布端口、外网连不出去'
