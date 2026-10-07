[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TermWrapPath,

    [switch]$Apply,

    [switch]$IAcceptUnsupportedWindowsClientPatch,

    [switch]$AllowRdpDisconnect
)

$ErrorActionPreference = 'Stop'
$terminalServerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$termServiceParametersKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters'
$firewallRuleName = 'AgentSeat-RDP-TCP-Private'

function Get-RegistryValueState {
    param([string]$Path, [string]$Name)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ exists = $false; value = $null; kind = $null }
    }

    $key = Get-Item -LiteralPath $Path
    try {
        $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($null -eq $value) {
            return [ordered]@{ exists = $false; value = $null; kind = $null }
        }

        return [ordered]@{
            exists = $true
            value = $value
            kind = $key.GetValueKind($Name).ToString()
        }
    }
    finally {
        $key.Close()
    }
}

$resolvedTermWrap = (Resolve-Path -LiteralPath $TermWrapPath).Path
if ([IO.Path]::GetFileName($resolvedTermWrap) -ne 'TermWrap.dll') {
    throw 'The dependency file must be named TermWrap.dll.'
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedTermWrap).Hash
$rdpPort = (Get-ItemProperty -LiteralPath "$terminalServerKey\WinStations\RDP-Tcp" -Name PortNumber -ErrorAction SilentlyContinue).PortNumber
if (-not $rdpPort) { $rdpPort = 3389 }

Write-Host 'Planned host changes:'
Write-Host "  Copy $resolvedTermWrap to %ProgramFiles%\agent-seat\dependencies\TermWrap.dll"
Write-Host '  Point TermService\Parameters\ServiceDll at that wrapper'
Write-Host '  Set fDenyTSConnections=0 and fSingleSessionPerUser=0'
Write-Host "  Add a private-profile inbound TCP firewall rule for port $rdpPort"
Write-Host '  Restart Remote Desktop Services (all current RDP sessions will disconnect)'
Write-Host "TermWrap SHA256: $hash"

if (-not $Apply) {
    Write-Warning 'Dry run only. No system state was changed.'
    Write-Warning 'Read docs/SETUP.md and re-run with -Apply -IAcceptUnsupportedWindowsClientPatch if you accept the risks.'
    exit 0
}

if (-not $IAcceptUnsupportedWindowsClientPatch) {
    throw 'The explicit -IAcceptUnsupportedWindowsClientPatch switch is required.'
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}

if ($env:SESSIONNAME -like 'RDP-*' -and -not $AllowRdpDisconnect) {
    throw 'This PowerShell is running through RDP. Use the physical console, or explicitly add -AllowRdpDisconnect.'
}

$installRoot = Join-Path $env:ProgramFiles 'agent-seat'
$dependencyDirectory = Join-Path $installRoot 'dependencies'
$destinationDll = Join-Path $dependencyDirectory 'TermWrap.dll'
$backupDirectory = Join-Path $env:ProgramData 'agent-seat\backups'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupFile = Join-Path $backupDirectory "multi-session-$timestamp.json"

$backup = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machineName = $env:COMPUTERNAME
    serviceDll = Get-RegistryValueState $termServiceParametersKey 'ServiceDll'
    denyConnections = Get-RegistryValueState $terminalServerKey 'fDenyTSConnections'
    singleSessionPerUser = Get-RegistryValueState $terminalServerKey 'fSingleSessionPerUser'
    firewallRuleExisted = [bool](Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue)
    dependencySha256 = $hash
}

New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
$backup | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backupFile -Encoding utf8
Write-Host "Recovery backup: $backupFile"

New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
Stop-Service -Name TermService -Force
try {
    Copy-Item -LiteralPath $resolvedTermWrap -Destination $destinationDll -Force
    New-ItemProperty -LiteralPath $termServiceParametersKey -Name ServiceDll -Value $destinationDll -PropertyType ExpandString -Force | Out-Null
    New-ItemProperty -LiteralPath $terminalServerKey -Name fDenyTSConnections -Value 0 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $terminalServerKey -Name fSingleSessionPerUser -Value 0 -PropertyType DWord -Force | Out-Null

    if (-not $backup.firewallRuleExisted) {
        New-NetFirewallRule -Name $firewallRuleName -DisplayName 'AgentSeat RDP (TCP, private networks)' `
            -Direction Inbound -Action Allow -Protocol TCP -LocalPort $rdpPort -Profile Private | Out-Null
    }
}
finally {
    try {
        Start-Service -Name TermService
        $service = Get-Service -Name TermService
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(15))
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        do {
            $listener = Get-NetTCPConnection -State Listen -LocalPort $rdpPort -ErrorAction SilentlyContinue
            if ($listener) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        if (-not $listener) {
            throw "TermService started but no RDP listener appeared on TCP $rdpPort."
        }
    }
    catch {
        $activationFailure = $_
        Write-Warning "TermWrap activation failed; restoring the backup immediately: $($activationFailure.Exception.Message)"
        & (Join-Path $PSScriptRoot 'Restore-MultiSession.ps1') -BackupFile $backupFile -Apply
        throw "TermWrap was rolled back because the RDP listener did not become healthy: $($activationFailure.Exception.Message)"
    }
}

Write-Host 'Multi-session host changes applied and the RDP listener passed its health check.'
Write-Host "To restore the exact registry values, run scripts\Restore-MultiSession.ps1 -BackupFile '$backupFile' -Apply"
