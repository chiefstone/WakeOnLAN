#requires -Version 5.1
<#
Run with Windows PowerShell 5.1 on 192.168.1.10.
Credentials stay in memory. Every shutdown requires an exact interactive confirmation.
Namespace permission checks do not prove the remote token holds SeShutdownPrivilege.
#>
[CmdletBinding()]
param(
    [string]$UserName,
    [switch]$CheckOnly,
    [switch]$ForceShutdown,
    [ValidateRange(1, 300)][int]$WmiTimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$target = '192.168.1.101'
if ($PSVersionTable.PSEdition -eq 'Core') {
    throw '请使用 Windows PowerShell 5.1：powershell.exe -NoProfile -File 本脚本路径'
}
if (-not (Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq '192.168.1.10')) {
    throw '本机未配置 192.168.1.10，请在指定的控制电脑运行。'
}

function Write-StageLog([string]$Text) {
    Write-Host ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Text)
}
function Test-ShutdownConfirmation([string]$Text) {
    $expected = if ($ForceShutdown) { 'FORCE SHUTDOWN 192.168.1.101' } else { 'SHUTDOWN 192.168.1.101' }
    return $Text -ceq $expected
}
function Test-RpcTcpConnection {
    param(
        [string]$SourceAddress = '192.168.1.10',
        [string]$RemoteAddress = '192.168.1.101',
        [int]$Port = 135,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 5
    )
    $endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($SourceAddress), 0)
    $client = [System.Net.Sockets.TcpClient]::new($endpoint)
    $waitHandle = $null
    try {
        $pending = $client.BeginConnect([System.Net.IPAddress]::Parse($RemoteAddress), $Port, $null, $null)
        $waitHandle = $pending.AsyncWaitHandle
        if (-not $waitHandle.WaitOne($TimeoutSeconds * 1000)) {
            throw "TCP ${RemoteAddress}:$Port 连接超时（$TimeoutSeconds 秒），源地址 $SourceAddress。"
        }
        $client.EndConnect($pending)
        Write-Host "TCP ${RemoteAddress}:$Port 连接成功，源地址 $SourceAddress。" -ForegroundColor Green
    } finally {
        $client.Close()
        if ($null -ne $waitHandle) { $waitHandle.Close() }
    }
}

Write-Host "目标电脑：$target；控制端应使用源地址 192.168.1.10。"
Write-Host 'Windows 为 WMI/DCOM 选择路由，下面显示当前路由和源地址。'
Find-NetRoute -RemoteIPAddress $target | Format-List IPAddress, InterfaceAlias, NextHop, DestinationPrefix
Write-Host '检查 TCP 135（RPC 端点映射服务），超时时间 5 秒……'
try {
    Test-RpcTcpConnection
} catch {
    Write-Warning $_.Exception.Message
    throw 'TCP 135 检查失败，尚未验证用户名和密码。请在远程电脑检查 RPC 监听、WMI 防火墙规则及有效策略。'
}
if (-not $UserName) {
    $UserName = Read-Host '请输入远程账户（远程电脑名\用户名 或 域\用户名）'
}
if ([string]::IsNullOrWhiteSpace($UserName)) { throw '用户名不能为空。' }
$credential = Get-Credential -UserName $UserName -Message "输入 $target 的账户密码"
if ($null -eq $credential) { Write-Host '已取消。'; return }
# Each operation runs in a separate process so a blocked DCOM call can be terminated.
$worker = {
    param($Target, $Credential, $Operation, $ForceShutdown)
    $ErrorActionPreference = 'Stop'
    [pscustomobject]@{Kind='Worker'; ProcessId=$PID}
    function Stage([string]$Name) { [pscustomobject]@{Kind='Stage'; Text=$Name} }
    function Log([string]$Text) { [pscustomobject]@{Kind='Log'; Text=$Text} }
    $connection = @{
        ComputerName=$Target; Credential=$Credential; Authentication='PacketPrivacy'
        Impersonation='Impersonate'; EnableAllPrivileges=$true; ErrorAction='Stop'
    }
    Stage '建立 DCOM/WMI 连接 root\cimv2（ManagementScope.Connect）'
    $options = New-Object System.Management.ConnectionOptions
    $options.Username = $Credential.UserName
    $options.SecurePassword = $Credential.Password
    $options.Authentication = [System.Management.AuthenticationLevel]::PacketPrivacy
    $options.Impersonation = [System.Management.ImpersonationLevel]::Impersonate
    $options.EnablePrivileges = $true
    $scope = [System.Management.ManagementScope]::new(('\\{0}\root\cimv2' -f $Target), $options)
    $scope.Connect()
    Log 'ManagementScope.Connect 成功。'
    Stage '提交 Win32_OperatingSystem 查询（ManagementObjectSearcher.Get）'
    $query = [System.Management.ObjectQuery]::new('SELECT * FROM Win32_OperatingSystem')
    $searcher = [System.Management.ManagementObjectSearcher]::new($scope, $query)
    $instances = $searcher.Get()
    Stage '读取 Win32_OperatingSystem 查询结果'
    $os = $null
    foreach ($instance in $instances) { $os = $instance; break }
    if ($null -eq $os) { throw '没有返回操作系统实例。' }
    Log "WMI/DCOM 连接成功：$($os.CSName)；$($os.Caption)；启动时间 $($os.LastBootUpTime)"
    if ($Operation -eq 'Shutdown') {
        $flags = if ($ForceShutdown) { 5 } else { 1 }
        Stage "发送 Win32Shutdown($flags, 0) 关机请求"
        $parameters = $os.psbase.GetMethodParameters('Win32Shutdown')
        $parameters['Flags'] = $flags
        $parameters['Reserved'] = 0
        $result = $os.psbase.InvokeMethod('Win32Shutdown', $parameters,
            [System.Management.InvokeMethodOptions]$null)
        if ($null -eq $result -or $null -eq $result.ReturnValue) {
            throw '未收到关机返回码，操作结果未知，请检查目标电脑状态。'
        }
        $code = [uint32]$result.ReturnValue
        if ($code -ne 0) {
            $hint = switch ($code) {
                2 { '拒绝访问。' }
                3 { '权限不足。' }
                1191 { '目标有其他已登录用户。请保存工作并注销相关会话，或显式选择 -ForceShutdown。' }
                default { ([System.ComponentModel.Win32Exception]::new([int]$code)).Message }
            }
            throw "Win32Shutdown 返回 $code。$hint"
        }
        Log '返回码 0：目标接受关机请求，请检查是否实际完成关机。'
        return
    }

    Stage '查询 __SystemSecurity'
    $security = Get-WmiObject -Namespace root\cimv2 -Class __SystemSecurity @connection
    Stage '调用 GetCallerAccessRights'
    $access = $security.psbase.InvokeMethod('GetCallerAccessRights',
        [System.Management.ManagementBaseObject]$null,
        [System.Management.InvokeMethodOptions]$null)
    if ($null -eq $access -or $null -eq $access.ReturnValue -or $access.ReturnValue -ne 0) {
        throw 'GetCallerAccessRights 未返回成功状态。'
    }
    $rights = [uint32]$access.Rights
    Log ('WMI 命名空间权限掩码：0x{0:X}' -f $rights)
    foreach ($entry in @(@{Name='启用账户'; Mask=1}, @{Name='执行方法'; Mask=2}, @{Name='远程启用'; Mask=32})) {
        Log ("{0}：{1}" -f $entry.Name, (($rights -band $entry.Mask) -eq $entry.Mask))
    }
    if (($rights -band 35) -ne 35) { throw 'WMI 命名空间权限不足。' }

    Stage '读取 Win32Shutdown 方法参数（只读取元数据）'
    $parameters = $os.psbase.GetMethodParameters('Win32Shutdown')
    if ($null -eq $parameters) { throw 'Win32Shutdown 未返回方法参数。' }
    Log 'Win32Shutdown 方法参数读取成功。'
    $parameters.Dispose()

    # UAC inspection is optional. All other failures stop the precheck.
    try {
        Stage '连接 root\default 并读取 StdRegProv'
        $registry = Get-WmiObject -Namespace root\default -Class StdRegProv -List @connection
        Stage '读取 LocalAccountTokenFilterPolicy'
        $parameters = $registry.psbase.GetMethodParameters('GetDWORDValue')
        $parameters['hDefKey'] = [uint32]2147483650
        $parameters['sSubKeyName'] = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
        $parameters['sValueName'] = 'LocalAccountTokenFilterPolicy'
        $uac = $registry.psbase.InvokeMethod('GetDWORDValue', $parameters,
            [System.Management.InvokeMethodOptions]$null)
        if ($null -eq $uac -or $null -eq $uac.ReturnValue) {
            throw '注册表查询未返回有效状态。'
        }
        if ($uac.ReturnValue -eq 0) {
            Log "LocalAccountTokenFilterPolicy=$($uac.uValue)"
        } elseif ($uac.ReturnValue -eq 2) {
            Log 'LocalAccountTokenFilterPolicy 未配置，使用 Windows 默认行为。'
        } else { throw "注册表查询返回 $($uac.ReturnValue)" }
    } catch { Log "UAC 可选诊断未完成：$($_.Exception.Message)" }
    Log '预检查完成。SeShutdownPrivilege 仍需实际关机调用验证。'
}

function Invoke-WmiWorker([string]$Operation) {
    $job = $null
    $workerProcess = $null
    $stage = '启动 WMI 测试子进程'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastNotice = 0
    Write-StageLog "$stage，单阶段超时 $WmiTimeoutSeconds 秒"
    try {
        $job = Start-Job -ScriptBlock $worker -ArgumentList $target, $credential, $Operation, ([bool]$ForceShutdown)
        while ($true) {
            $workerErrors = @()
            $records = @(Receive-Job -Job $job -ErrorAction SilentlyContinue -ErrorVariable workerErrors)
            foreach ($record in $records) {
                switch ($record.Kind) {
                    'Worker' {
                        # A fast worker may already have exited before its output is read.
                        $workerProcess = Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
                    }
                    'Stage' {
                        Write-StageLog ("上一阶段耗时 {0:N1} 秒" -f $clock.Elapsed.TotalSeconds)
                        $stage = $record.Text
                        $clock.Restart()
                        $lastNotice = 0
                        Write-StageLog "开始：$stage"
                    }
                    'Log' { Write-StageLog $record.Text }
                }
            }
            # Drain stage records before reporting an error from the same output batch.
            if ($workerErrors.Count) { throw $workerErrors[0] }
            # Completed/failed jobs may still have unread output.
            if ($job.State -in @('Completed', 'Failed', 'Stopped') -and -not $job.HasMoreData) {
                if ($job.State -ne 'Completed') { throw "子进程状态：$($job.State)" }
                Write-StageLog ("完成：$stage，耗时 {0:N1} 秒" -f $clock.Elapsed.TotalSeconds)
                break
            }
            if ($clock.Elapsed.TotalSeconds -ge $WmiTimeoutSeconds) {
                throw "阶段超时：$stage，超过 $WmiTimeoutSeconds 秒。"
            }
            if ($clock.Elapsed.TotalSeconds -ge ($lastNotice + 5)) {
                $lastNotice = [int]$clock.Elapsed.TotalSeconds
                Write-StageLog "仍在等待：$stage，已等待 $lastNotice 秒"
            }
            Start-Sleep -Milliseconds 100
        }
    } catch {
        throw "WMI 阶段 [$stage] 失败：$($_.Exception.Message)"
    } finally {
        if ($null -ne $job) {
            # Stop the process before removing the job. Stop-Job can itself wait on DCOM.
            if ($null -ne $workerProcess) {
                try {
                    if (-not $workerProcess.HasExited) { $workerProcess.Kill() }
                } finally { $workerProcess.Dispose() }
            }
            Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-StageLog '凭据已输入，开始 WMI 分阶段测试'
try {
    Invoke-WmiWorker 'Check'
    if ($CheckOnly) { Write-StageLog '只检查模式结束。'; return }
    Write-Warning "将请求关闭 $target，请先保存远程电脑上的工作。"
    $expected = 'SHUTDOWN 192.168.1.101'
    if ($ForceShutdown) {
        Write-Warning '强制关机会结束所有用户的应用，未保存的数据可能丢失。'
        $expected = 'FORCE SHUTDOWN 192.168.1.101'
    }
    $confirmation = Read-Host "输入 $expected 确认关机，其他输入取消"
    if (-not (Test-ShutdownConfirmation $confirmation)) {
        Write-StageLog '已取消关机测试。'
        return
    }
    try { Invoke-WmiWorker 'Shutdown' }
    catch {
        Write-Warning '关机阶段报错或超时，目标可能已经收到请求。请检查实际状态，再决定是否重试。'
        throw
    }
} finally {
    $credential = $null
}
