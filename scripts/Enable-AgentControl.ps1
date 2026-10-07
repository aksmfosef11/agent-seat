[CmdletBinding()]
param(
    # Where the service (and the agent-seat CLI) look for the bearer token that guards the agent API.
    [string]$TokenFile = (Join-Path $env:ProgramData 'agent-seat\agent-token.txt'),

    # Accounts that may read the token besides SYSTEM and Administrators: the person who runs the AI
    # tools. Never list a seat account; that would let a seat user drive the agent seat.
    [string[]]$ReaderAccounts = @("$env:USERDOMAIN\$env:USERNAME"),

    # Replace an existing token. The old one stops working immediately.
    [switch]$Rotate,

    # Approve a seat for agent control. The seat id AND its Windows account are recorded together; the service
    # refuses any seat whose id/account pair is not on this administrator-owned list. Without an entry nothing
    # can be agent-controlled, whatever the management API or the seat's own settings say.
    [string]$AllowSeat,
    [string]$AllowUser,
    [string]$AllowlistFile = (Join-Path $env:ProgramData 'agent-seat\agent-seats.json'),

    # Parent of the per-seat file hand-off folders (<ShareRoot>\<seat id>); see `agent-seat computer files`.
    [string]$ShareRoot = (Join-Path $env:ProgramData 'agent-seat\agent-share'),

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if ([bool]$AllowSeat -ne [bool]$AllowUser) { throw '-AllowSeat and -AllowUser must be given together.' }
if ($AllowSeat -and $AllowSeat -notmatch '^[a-z][a-z0-9-]{0,31}$') { throw "Seat id '$AllowSeat' is not valid." }
$exists = Test-Path -LiteralPath $TokenFile
Write-Host "Agent token file:  $TokenFile ($(if ($exists -and -not $Rotate) { 'exists, kept' } elseif ($exists) { 'will be replaced' } else { 'will be created' }))"
Write-Host "Readable by:       SYSTEM, Administrators, $($ReaderAccounts -join ', ')"
Write-Host 'The token is never printed. Use agent-seat computer view to open the web UI without copying it.'
if ($AllowSeat) {
    Write-Host "Approved seat:     '$AllowSeat' runs as Windows user '$AllowUser' (recorded in $AllowlistFile)"
    Write-Host "File folder:       $(Join-Path $ShareRoot $AllowSeat) (owner + seat account only; the seat account cannot delete or rename the folder itself)"
}
else { Write-Host "Approved seats:    unchanged (add one with -AllowSeat <id> -AllowUser <windows-user>); list: $AllowlistFile" }

if (-not $Apply) {
    Write-Warning 'Dry run only. Nothing was changed. Add -Apply to create or secure the token file.'
    exit 0
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}

$directory = Split-Path -Parent $TokenFile
New-Item -ItemType Directory -Path $directory -Force | Out-Null

# Create the file first and lock it down BEFORE the secret is written into it.
if (-not $exists) { New-Item -ItemType File -Path $TokenFile | Out-Null }
$acl = [Security.AccessControl.FileSecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$system = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
$admins = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
foreach ($sid in @($system, $admins)) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
}
foreach ($account in $ReaderAccounts) {
    $identity = [Security.Principal.NTAccount]::new($account)
    $null = $identity.Translate([Security.Principal.SecurityIdentifier])   # fail early on a typo
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'Read', 'Allow'))
}
Set-Acl -LiteralPath $TokenFile -AclObject $acl

if (-not $exists -or $Rotate) {
    $bytes = [byte[]]::new(32)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()   # GetBytes works in Windows PowerShell 5.1 and 7 (Fill is .NET Core only)
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [Array]::Clear($bytes, 0, $bytes.Length)
    [IO.File]::WriteAllText($TokenFile, $token, [Text.UTF8Encoding]::new($false))
    $token = $null
    Write-Host 'A new agent token was written.'
}
else {
    Write-Host 'The existing token was kept; its permissions were re-applied.'
}

if ($AllowSeat) {
    $entries = @()
    if (Test-Path -LiteralPath $AllowlistFile) {
        try { $existing = Get-Content -LiteralPath $AllowlistFile -Raw | ConvertFrom-Json }
        catch { throw "The existing allow list '$AllowlistFile' is not valid JSON; fix or delete it first." }
        $entries = @($existing.seats | Where-Object { $_ -and $_.seatId -ne $AllowSeat })
    }
    $entries += [pscustomobject]@{ seatId = $AllowSeat; userName = ($AllowUser -replace '^.*\\', '') }
    if (-not (Test-Path -LiteralPath $AllowlistFile)) { New-Item -ItemType File -Path $AllowlistFile | Out-Null }
    # Lock the list down first: only administrators may change what an agent is allowed to drive.
    $listAcl = [Security.AccessControl.FileSecurity]::new()
    $listAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($system, $admins)) {
        $listAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    }
    Set-Acl -LiteralPath $AllowlistFile -AclObject $listAcl
    [IO.File]::WriteAllText($AllowlistFile, (@{ seats = $entries } | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Write-Host "Approved seat '$AllowSeat' for Windows user '$($AllowUser -replace '^.*\\', '')'."

    # --- the seat's file hand-off folder
    # Only the owner and the seat account may write inside it. The seat account may create things in the folder and
    # change what is inside, but can neither delete nor rename the folder itself: that is what stops it swapping
    # the folder for a link that the SYSTEM service would then delete through.
    $seatAccount = [Security.Principal.NTAccount]::new("$env:COMPUTERNAME\$($AllowUser -replace '^.*\\', '')")
    try { $null = $seatAccount.Translate([Security.Principal.SecurityIdentifier]) }
    catch { throw "Windows user '$seatAccount' does not exist, so its file folder cannot be secured." }
    $both = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $noInherit = [Security.AccessControl.InheritanceFlags]::None
    $noProp = [Security.AccessControl.PropagationFlags]::None
    $childrenOnly = [Security.AccessControl.PropagationFlags]::InheritOnly
    function New-Rule($identity, $rights, $inherit, $propagate) {
        [Security.AccessControl.FileSystemAccessRule]::new($identity, [Security.AccessControl.FileSystemRights]$rights, $inherit, $propagate, 'Allow')
    }

    New-Item -ItemType Directory -Path $ShareRoot -Force | Out-Null
    $rootAcl = [Security.AccessControl.DirectorySecurity]::new()
    $rootAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($system, $admins)) { $rootAcl.AddAccessRule((New-Rule $sid 'FullControl' $both $noProp)) }
    $users = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    $rootAcl.AddAccessRule((New-Rule $users 'ReadAndExecute' $noInherit $noProp))   # lets people reach their own folder
    Set-Acl -LiteralPath $ShareRoot -AclObject $rootAcl

    $seatDir = Join-Path $ShareRoot $AllowSeat
    New-Item -ItemType Directory -Path $seatDir -Force | Out-Null
    $seatAcl = [Security.AccessControl.DirectorySecurity]::new()
    $seatAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($system, $admins)) { $seatAcl.AddAccessRule((New-Rule $sid 'FullControl' $both $noProp)) }
    foreach ($account in $ReaderAccounts) {
        $seatAcl.AddAccessRule((New-Rule ([Security.Principal.NTAccount]::new($account)) 'Modify' $both $noProp))
    }
    # The folder itself: list and create entries, but no Delete and no DeleteSubdirectoriesAndFiles.
    $seatAcl.AddAccessRule((New-Rule $seatAccount 'ReadAndExecute, WriteData, AppendData' $noInherit $noProp))
    # Everything inside it: full change rights, inherited by whatever is created there.
    $seatAcl.AddAccessRule((New-Rule $seatAccount 'Modify' $both $childrenOnly))
    Set-Acl -LiteralPath $seatDir -AclObject $seatAcl
    Write-Host "File folder for seat '$AllowSeat': $seatDir (owner and $seatAccount may write inside it)"
}

Write-Host ''
Write-Host 'The service re-reads the file on change, so no restart is needed (the build must include agent control).'
Write-Host 'Next steps:'
Write-Host '  agent-seat agent-control <seat-id> on        opt a seat in (or create one: agent-seat add <id> <user> --agent)'
Write-Host '  agent-seat computer start                    bring the agent seat up'
Write-Host '  agent-seat computer guide                    what to tell the AI'
