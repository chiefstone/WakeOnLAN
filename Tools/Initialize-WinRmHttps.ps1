& {
    $ErrorActionPreference = 'Stop'
    $target = '__TARGET_IPV4__'
    $controller = '__CONTROLLER_IPV4__'
    if (-not (Get-NetIPAddress -IPAddress $target -ErrorAction SilentlyContinue)) {
        throw "Run this script on the target computer with IPv4 address $target."
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in an elevated Windows PowerShell session.'
    }

    Set-Service WinRM -StartupType Automatic
    Start-Service WinRM

    $listener = Get-WSManInstance -ResourceURI winrm/config/Listener -Enumerate |
        Where-Object Transport -eq 'HTTPS' | Select-Object -First 1
    if ($listener) {
        $cert = Get-Item "Cert:\LocalMachine\My\$($listener.CertificateThumbprint)"
    } else {
        $name = $env:COMPUTERNAME
        $cert = New-SelfSignedCertificate -Type SSLServerAuthentication -Subject "CN=$name" `
            -TextExtension "2.5.29.17={text}DNS=$name&IPAddress=$target" `
            -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy NonExportable
        New-WSManInstance winrm/config/Listener `
            -SelectorSet @{Transport='HTTPS'; Address='*'} `
            -ValueSet @{Hostname=$name; CertificateThumbprint=$cert.Thumbprint} | Out-Null
    }

    $rule = 'WakeOnLAN-WinRM-HTTPS-5986'
    if (Get-NetFirewallRule -PolicyStore PersistentStore -Name $rule -ErrorAction SilentlyContinue) {
        Set-NetFirewallRule -PolicyStore PersistentStore -Name $rule -Enabled True -Direction Inbound `
            -Action Allow -Profile Any -Protocol TCP -LocalPort 5986 -LocalAddress $target -RemoteAddress $controller
    } else {
        New-NetFirewallRule -PolicyStore PersistentStore -Name $rule -DisplayName 'WakeOnLAN WinRM HTTPS from controller' `
            -Direction Inbound -Action Allow -Profile Any -Protocol TCP -LocalPort 5986 `
            -LocalAddress $target -RemoteAddress $controller | Out-Null
    }

    $path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    if ((Get-ItemProperty -Path $path -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy -ne 1) {
        Write-Warning 'Optional: allowing full network administration affects ALL local administrators on this PC, including outside WinRM.'
        if ((Read-Host 'For a local administrator account, type ENABLE to allow full remote tokens, or press Enter to keep the current policy') -ceq 'ENABLE') {
            New-ItemProperty -Path $path -Name LocalAccountTokenFilterPolicy -PropertyType DWord -Value 1 -Force | Out-Null
        }
    }

    $policy = (Get-ItemProperty -Path $path -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
    if ($null -eq $policy) {
        'LocalAccountTokenFilterPolicy: not set (Windows default).'
    } else {
        "LocalAccountTokenFilterPolicy: $policy"
    }

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        'SHA256: ' + ([BitConverter]::ToString($sha256.ComputeHash($cert.RawData)) -replace '-', '')
    } finally {
        $sha256.Dispose()
    }
}
