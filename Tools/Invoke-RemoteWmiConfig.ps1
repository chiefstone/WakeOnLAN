$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Read-InputValue {
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::ReadLine()))
}

try {
    $transport = Read-InputValue
    $computerName = Read-InputValue
    $targetAddress = Read-InputValue
    $userName = Read-InputValue
    $password = [Security.SecureString]::new()
    foreach ($character in (Read-InputValue).ToCharArray()) {
        $password.AppendChar($character)
    }
    $password.MakeReadOnly()
    $allowedAddresses = Read-InputValue
    $source = Read-InputValue
    $operation = Read-InputValue
    if ($operation -notin 'Apply', 'Verify') { throw 'Unsupported Remote Config operation.' }
    $connection = @{
        ComputerName = $computerName
        SessionOption = New-PSSessionOption -OpenTimeout 15000
    }
    switch ($transport) {
        'HTTP' { $connection.Authentication = 'Kerberos' }
        'HTTPS' {
            $connection.UseSSL = $true
            $connection.Authentication = 'Negotiate'
            $connection.Credential = [pscredential]::new($userName, $password)
        }
        default { throw 'Unsupported WinRM transport.' }
    }
    try {
        $session = New-PSSession @connection
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 2
    }

    try {
        Invoke-Command -Session $session -ScriptBlock {
            param($Source, $Target, $Account, $Controllers, $Operation)

            $ErrorActionPreference = 'Stop'
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = [Security.Principal.WindowsPrincipal]::new($identity)
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'Remote Config requires an administrator session on the target PC.'
            }
            if ($Operation -eq 'Apply') {
                & ([scriptblock]::Create($Source)) -Mode Apply -TargetAddress $Target -Account $Account -AllowedAddress ($Controllers.Split(','))
                return
            }

            if (-not (Get-NetIPAddress -IPAddress $Target -ErrorAction SilentlyContinue)) {
                throw 'The WinRM session did not reach the selected target IPv4 address.'
            }
            "WinRM administrator session verified on $env:COMPUTERNAME."
            $path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
            $policy = (Get-ItemProperty -Path $path -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
            if ($null -eq $policy) {
                'LocalAccountTokenFilterPolicy: not set (Windows default).'
            } else {
                "LocalAccountTokenFilterPolicy: $policy"
            }
            if ($identity.Name.StartsWith("$env:COMPUTERNAME\", [StringComparison]::OrdinalIgnoreCase) -and $policy -ne 1) {
                throw 'LocalAccountTokenFilterPolicy is not 1 for this local administrator. Review the manual target setup.'
            }
            $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Name 'WakeOnLAN-WMI-*' -ErrorAction SilentlyContinue)
            foreach ($prefix in 'WakeOnLAN-WMI-RPC-*', 'WakeOnLAN-WMI-Service-*') {
                $matching = @($rules | Where-Object Name -Like $prefix)
                if ($matching.Count -ne 1 -or $matching[0].Enabled -ne 'True' -or
                    $matching[0].Direction -ne 'Inbound' -or $matching[0].Action -ne 'Allow') {
                    throw 'Managed WMI firewall rules are missing or disabled. Apply Remote Config first.'
                }
                $sources = @($matching[0] | Get-NetFirewallAddressFilter | Select-Object -ExpandProperty RemoteAddress)
                foreach ($controller in $Controllers.Split(',')) {
                    if ($controller -notin $sources) { throw "WMI firewall rule does not explicitly allow $controller." }
                }
            }
            "Managed WMI firewall rules allow $Controllers."
        } -ArgumentList $source, $targetAddress, $userName, $allowedAddresses, $operation
        if ($operation -eq 'Verify') {
            Add-Type -AssemblyName System.Management
            $options = [System.Management.ConnectionOptions]::new()
            $options.Username = $userName
            $options.SecurePassword = $password
            $options.Authentication = [System.Management.AuthenticationLevel]::PacketPrivacy
            $options.Impersonation = [System.Management.ImpersonationLevel]::Impersonate
            $scope = [System.Management.ManagementScope]::new(('\\{0}\root\cimv2' -f $targetAddress), $options)
            $query = [System.Management.ObjectQuery]::new('SELECT CSName, LastBootUpTime FROM Win32_OperatingSystem')
            $searchOptions = [System.Management.EnumerationOptions]::new()
            $searchOptions.Timeout = [TimeSpan]::FromSeconds(5)
            $searcher = [System.Management.ManagementObjectSearcher]::new($scope, $query, $searchOptions)
            try {
                $systems = $searcher.Get()
                try {
                    $read = $false
                    foreach ($os in $systems) {
                        try {
                            if (-not $os['LastBootUpTime']) { throw 'Windows boot time is missing.' }
                            $parameters = $os.GetMethodParameters('Win32Shutdown')
                            if (-not $parameters) { throw 'Win32Shutdown method metadata is missing.' }
                            $parameters.Dispose()
                            "WMI verified on $($os['CSName']): Windows boot time and Win32Shutdown method metadata returned."
                            $read = $true
                            break
                        } finally { $os.Dispose() }
                    }
                    if (-not $read) { throw 'No Windows operating system instance was returned.' }
                } finally { $systems.Dispose() }
            } finally { $searcher.Dispose() }
        }
    } catch {
        [Console]::Error.WriteLine($_.Exception.Message)
        exit 3
    } finally {
        Remove-PSSession -Session $session -ErrorAction SilentlyContinue
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
