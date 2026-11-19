#requires -Version 5.1
#requires -RunAsAdministrator
<#
Run in 64-bit Windows PowerShell 5.1 on the target computer.
Apply requires an existing local administrator account, the target PC IPv4 address,
and the controller IP addresses allowed through the firewall. No password is stored.
Restore replaces the saved ACLs and the two saved user-right assignments.
Use only a backup created by this script on this computer and kept in a trusted location.
#>
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Restore')][string]$Mode = 'Check',
    [string]$Account,
    [string]$BackupPath,
    [string]$TargetAddress,
    [string[]]$AllowedAddress
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or -not [Environment]::Is64BitProcess) {
    throw 'Use 64-bit Windows PowerShell 5.1 (powershell.exe).'
}
$ole = 'SOFTWARE\Microsoft\Ole'
$uac = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$dcomNames = @('DefaultAccessPermission', 'DefaultLaunchPermission',
    'MachineAccessRestriction', 'MachineLaunchRestriction')
$registryItems = @($dcomNames | ForEach-Object { @{Path=$ole; Name=$_} }) + @(
    @{Path=$ole; Name='EnableDCOM'}, @{Path=$uac; Name='LocalAccountTokenFilterPolicy'})
$security = Get-WmiObject -Namespace root\cimv2 -Class __SystemSecurity -EnableAllPrivileges

function Assert-Result($Result, [string]$Operation) {
    if ($null -eq $Result -or $null -eq $Result.ReturnValue) {
        throw "$Operation did not return a WMI status."
    }
    if ($Result.ReturnValue -ne 0) { throw "$Operation failed: $($Result.ReturnValue)" }
}
function Read-WmiSecurity {
    $security.psbase.InvokeMethod('GetSD', [System.Management.ManagementBaseObject]$null,
        [System.Management.InvokeMethodOptions]$null)
}
function Write-WmiSecurity([byte[]]$Descriptor) {
    $parameters = $security.psbase.GetMethodParameters('SetSD')
    try {
        $parameters['SD'] = $Descriptor
        $security.psbase.InvokeMethod('SetSD', $parameters,
            [System.Management.InvokeMethodOptions]$null)
    } finally { $parameters.Dispose() }
}
function Read-Registry($Item) {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Item.Path)
    try {
        $exists = $null -ne $key -and $key.GetValueNames() -contains $Item.Name
        [pscustomobject]@{ Path=$Item.Path; Name=$Item.Name; Exists=$exists
            Value=$(if ($exists) { $key.GetValue($Item.Name) })
            Kind=$(if ($exists) { $key.GetValueKind($Item.Name).ToString() }) }
    } finally { if ($key) { $key.Dispose() } }
}
function Write-Registry($Item) {
    $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($Item.Path)
    try {
        if ($Item.Exists) {
            $kind = [Microsoft.Win32.RegistryValueKind]$Item.Kind
            $value = $Item.Value
            if ($kind -eq 'Binary') { $value = [byte[]]$value }
            $key.SetValue($Item.Name, $value, $kind)
        } else { $key.DeleteValue($Item.Name, $false) }
    } finally { $key.Dispose() }
}
function Grant-Acl([byte[]]$Bytes, [int]$Mask, $Sid, [switch]$Dcom) {
    $sd = [System.Security.AccessControl.CommonSecurityDescriptor]::new($false, $false, $Bytes, 0)
    if (-not $sd.IsDiscretionaryAclCanonical) { throw 'Noncanonical ACL requires manual review.' }
    if ($Dcom) {
        # Old DCOM execute-only ACEs mean all rights. Convert before mixing formats.
        $raw = [System.Security.AccessControl.RawSecurityDescriptor]::new($Bytes, 0)
        foreach ($ace in $raw.DiscretionaryAcl) {
            if ($ace -is [System.Security.AccessControl.CommonAce] -and $ace.AccessMask -eq 1) {
                $ace.AccessMask = $(if ($Mask -eq 5) { 7 } else { 31 })
            }
        }
        $converted = New-Object byte[] $raw.BinaryLength
        $raw.GetBinaryForm($converted, 0)
        $sd = [System.Security.AccessControl.CommonSecurityDescriptor]::new($false, $false, $converted, 0)
    }
    $sd.DiscretionaryAcl.AddAccess([System.Security.AccessControl.AccessControlType]::Allow,
        $Sid, $Mask, [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]::None)
    $output = New-Object byte[] $sd.BinaryLength
    $sd.GetBinaryForm($output, 0)
    return ,$output
}
function Export-Rights([string]$Path) {
    & secedit.exe /export /cfg $Path /areas USER_RIGHTS /quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export local security policy.' }
    $rights = @{}
    foreach ($name in @('SeShutdownPrivilege', 'SeRemoteShutdownPrivilege')) {
        $line = Get-Content -LiteralPath $Path | Where-Object { $_ -match "^$name\s*=" }
        $rights[$name] = $(if ($line) { ($line -split '=', 2)[1].Trim() } else { '' })
    }
    return $rights
}
function Write-Rights($Rights, [string]$Directory) {
    $inf = Join-Path $Directory 'rights-apply.inf'
    $db = Join-Path $Directory ('rights-' + [guid]::NewGuid().ToString('N') + '.sdb')
    $lines = @('[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"',
        'Revision=1', '[Privilege Rights]')
    foreach ($name in @('SeShutdownPrivilege', 'SeRemoteShutdownPrivilege')) {
        $lines += "$name = $($Rights[$name])"
    }
    $lines | Set-Content -LiteralPath $inf -Encoding Unicode
    & secedit.exe /configure /db $db /cfg $inf /areas USER_RIGHTS /quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot apply user rights. Check secedit logs.' }
}

if ($Mode -eq 'Restore') {
    if (-not $BackupPath) { throw 'Restore requires -BackupPath.' }
    $state = Import-Clixml -LiteralPath (Join-Path $BackupPath 'state.xml')
    if ($state.Computer -ne $env:COMPUTERNAME -or $state.Version -ne 1) {
        throw 'Backup belongs to a different computer or script version.'
    }
    foreach ($item in $state.Registry) { Write-Registry $item }
    $result = Write-WmiSecurity ([byte[]]$state.WmiSD)
    Assert-Result $result 'Restore WMI ACL'
    Write-Rights $state.Rights $BackupPath
    foreach ($rule in $state.Firewall) {
        Get-NetFirewallRule -PolicyStore PersistentStore -Name $rule.Name |
            Set-NetFirewallRule -Enabled ([string]$rule.Enabled)
    }
    foreach ($name in $state.CreatedRules) {
        Get-NetFirewallRule -PolicyStore PersistentStore -Name $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule
    }
    Write-Host 'Restore completed. Restart Windows to renew tokens and DCOM configuration.'
    return
}

if ($Mode -eq 'Apply' -and (-not $TargetAddress -or -not $AllowedAddress)) {
    throw 'Apply requires -TargetAddress (this target PC IPv4 address) and -AllowedAddress (controller IP addresses).'
}
$localAddresses = @(Get-NetIPAddress -AddressFamily IPv4)
if ($TargetAddress -and $TargetAddress -notin $localAddresses.IPAddress) {
    throw "Run this script on the target PC with IPv4 address $TargetAddress."
}
if ($AllowedAddress) {
    $AllowedAddress = @($AllowedAddress | ForEach-Object {
        $ip = $null
        if (-not [System.Net.IPAddress]::TryParse($_, [ref]$ip) -or
            $ip.Equals([System.Net.IPAddress]::Any) -or $ip.Equals([System.Net.IPAddress]::IPv6Any)) {
            throw "Specify an individual controller IP address. Invalid address: $_"
        }
        $ip.ToString()
    } | Select-Object -Unique)
}

Get-Service RpcSs, DcomLaunch, Winmgmt | Format-Table Name, Status
$localAddresses | Format-Table InterfaceAlias, IPAddress
$registryItems | ForEach-Object { Read-Registry $_ } | Format-Table Name, Exists, Value
if (-not $Account) { throw 'Specify -Account COMPUTER\User for Check or Apply.' }
$sid = ([System.Security.Principal.NTAccount]$Account).Translate(
    [System.Security.Principal.SecurityIdentifier])
$admins = Get-LocalGroup -SID 'S-1-5-32-544'
$members = @(Get-LocalGroupMember -Group $admins.Name)
if ($sid.Value -notin @($members | ForEach-Object { $_.SID.Value })) {
    throw 'Account must be a direct member of local Administrators. Script does not grant administrator membership.'
}
$localUser = Get-LocalUser | Where-Object { $_.SID.Value -eq $sid.Value }
if (-not $localUser -or -not $localUser.Enabled) { throw 'Specify an enabled local account.' }
$aclResult = Read-WmiSecurity
Assert-Result $aclResult 'Read WMI ACL'
if (-not $aclResult.SD -or $aclResult.SD.Length -eq 0) {
    throw 'WMI returned an empty security descriptor. No configuration was changed.'
}
# Require built-in service-bound templates rather than opening the entire RPC port range.
$templates = @(Get-NetFirewallRule -PolicyStore PersistentStore -Name 'WMI-RPCSS-In-TCP', 'WMI-WINMGMT-In-TCP')
if ($templates.Count -ne 2) { throw 'Expected built-in WMI firewall templates are missing.' }
Write-Host "Account: $Account ($($sid.Value)); allowed sources: $($AllowedAddress -join ', ')"
if ($Mode -eq 'Check') {
    $templates | Format-Table Name, Enabled, Profile
    Write-Host 'Check completed. Apply creates a backup before changing settings.'
    return
}
if (Get-NetFirewallRule -PolicyStore PersistentStore -Name 'WakeOnLAN-WMI-*' -ErrorAction SilentlyContinue) {
    throw 'A previous configuration exists. Restore its backup before applying again.'
}
if (-not $BackupPath) {
    $BackupPath = Join-Path $env:ProgramData ('WakeOnLAN-WmiBackup\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
if (Test-Path -LiteralPath $BackupPath) { throw 'Apply requires a new backup directory.' }
New-Item -ItemType Directory -Path $BackupPath | Out-Null
# Only SYSTEM and local administrators may change the recovery data.
& icacls.exe $BackupPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect backup directory.' }
$rights = Export-Rights (Join-Path $BackupPath 'user-rights-before.inf')
$wmiRules = @(Get-NetFirewallRule -PolicyStore PersistentStore -Name 'WMI-*' |
    Where-Object Direction -eq Inbound)
$id = [guid]::NewGuid().ToString('N')
$state = [pscustomobject]@{
    Version=1; Computer=$env:COMPUTERNAME; Account=$Account
    Registry=@($registryItems | ForEach-Object { Read-Registry $_ })
    WmiSD=[byte[]]$aclResult.SD; Rights=$rights
    Firewall=@($wmiRules | Select-Object Name, Enabled)
    CreatedRules=@("WakeOnLAN-WMI-RPC-$id", "WakeOnLAN-WMI-Service-$id")
}
$state | Export-Clixml -LiteralPath (Join-Path $BackupPath 'state.xml')
Write-Host "Backup saved: $BackupPath"
try {
    foreach ($name in $dcomNames) {
        $item = Read-Registry @{Path=$ole; Name=$name}
        if ($item.Exists) {
            $mask = $(if ($name -match 'Access') { 5 } else { 21 })
            $item.Value = Grant-Acl ([byte[]]$item.Value) $mask $sid -Dcom
            Write-Registry $item
        } else {
            Write-Host "$name uses Windows defaults; administrator defaults retained."
        }
    }
    Write-Registry @{Path=$ole; Name='EnableDCOM'; Exists=$true; Kind='String'; Value='Y'}
    Write-Registry @{Path=$uac; Name='LocalAccountTokenFilterPolicy'; Exists=$true; Kind='DWord'; Value=1}
    $newSD = Grant-Acl ([byte[]]$aclResult.SD) 35 $sid
    Assert-Result (Write-WmiSecurity $newSD) 'Grant WMI Enable Account / Execute Methods / Remote Enable'
    $newRights = @{}
    foreach ($name in $rights.Keys) {
        $entries = @($rights[$name] -split ',' | Where-Object { $_ })
        $newRights[$name] = (@($entries) + "*$($sid.Value)" | Select-Object -Unique) -join ','
    }
    Write-Rights $newRights $BackupPath
    # Clone service/program-bound built-in rules, preserving their RPC restrictions.
    for ($index = 0; $index -lt $templates.Count; $index++) {
        $name = $state.CreatedRules[$index]
        $templates[$index] | Copy-NetFirewallRule -NewName $name | Out-Null
        # Select the new rule explicitly instead of depending on Copy's pipeline output.
        Get-NetFirewallRule -PolicyStore PersistentStore -Name $name |
            Set-NetFirewallRule -Enabled True -Profile Any -RemoteAddress $AllowedAddress -Action Allow
        $created = Get-NetFirewallRule -PolicyStore PersistentStore -Name $name
        $addresses = @($created | Get-NetFirewallAddressFilter |
            Select-Object -ExpandProperty RemoteAddress)
        if ([string]$created.Enabled -ne 'True' -or
            [string]$created.Profile -ne 'Any' -or
            @(Compare-Object ($AllowedAddress | Sort-Object) ($addresses | Sort-Object)).Count) {
            throw "Firewall rule verification failed: $name"
        }
    }
    $wmiRules | Set-NetFirewallRule -Enabled False
    Write-Host 'Apply completed. Restart Windows, then test WMI from a permitted controller.'
    Write-Warning 'UAC remote filtering is disabled for local administrators on this computer. DCOM ACL changes affect machine-wide COM permissions.'
    Write-Warning 'Other firewall rules and domain policy can still allow or block WMI. Review effective policy if strict source isolation is required.'
} catch {
    Write-Warning "Apply stopped after a possible partial change. Restore with -Mode Restore -BackupPath '$BackupPath'."
    throw
}
