[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupFile,

    [switch]$Apply,

    [switch]$AllowRdpDisconnect
)

$ErrorActionPreference = 'Stop'
$terminalServerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$termServiceParametersKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters'
$firewallRuleName = 'AgentSeat-RDP-TCP-Private'

function Restore-RegistryValue {
    param([string]$Path, [string]$Name, [object]$State)

    if ($State.exists) {
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $State.value -PropertyType $State.kind -Force | Out-Null
    }
    else {
        Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

$resolvedBackup = (Resolve-Path -LiteralPath $BackupFile).Path
$backup = Get-Content -LiteralPath $resolvedBackup -Raw | ConvertFrom-Json
if ($backup.schemaVersion -ne 1) {
    throw "Unsupported backup schema: $($backup.schemaVersion)"
}
if ($backup.machineName -ne $env:COMPUTERNAME) {
    throw "Backup belongs to '$($backup.machineName)', not '$env:COMPUTERNAME'."
}

Write-Host "Backup: $resolvedBackup"
Write-Host "Restore ServiceDll to: $($backup.serviceDll.value)"
Write-Host "Restore fDenyTSConnections to: $($backup.denyConnections.value)"
Write-Host "Restore fSingleSessionPerUser to: $($backup.singleSessionPerUser.value)"
Write-Host 'Remote Desktop Services will restart and current RDP sessions will disconnect.'

if (-not $Apply) {
    Write-Warning 'Dry run only. No system state was changed. Add -Apply to restore.'
    exit 0
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}
if ($env:SESSIONNAME -like 'RDP-*' -and -not $AllowRdpDisconnect) {
    throw 'This PowerShell is running through RDP. Use the physical console, or explicitly add -AllowRdpDisconnect.'
}

Stop-Service -Name TermService -Force
try {
    Restore-RegistryValue $termServiceParametersKey 'ServiceDll' $backup.serviceDll
    Restore-RegistryValue $terminalServerKey 'fDenyTSConnections' $backup.denyConnections
    Restore-RegistryValue $terminalServerKey 'fSingleSessionPerUser' $backup.singleSessionPerUser
    if (-not $backup.firewallRuleExisted) {
        Remove-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue
    }
}
finally {
    Start-Service -Name TermService
}

Write-Host 'Original registry values were restored. The copied dependency was retained for recoverability.'
