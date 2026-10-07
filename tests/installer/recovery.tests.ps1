# Real filesystem/DPAPI, simulated Windows services/accounts/tasks. No host installation or reboot.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'Install.ps1')
$testRoot = Join-Path $repoRoot ('artifacts\recovery-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$originalProgramFiles = $env:ProgramFiles; $originalProgramData = $env:ProgramData
$script:passed = 0
function Check([string]$Name, [scriptblock]$Body) { & $Body; $script:passed++; Write-Host "PASS $Name" }
function Expect-Failure([scriptblock]$Body) {
    $failed = $false
    try { & $Body | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected refusal' }
}
function New-Harness {
    $root = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
    $env:ProgramFiles = Join-Path $root 'ProgramFiles'; $env:ProgramData = Join-Path $root 'ProgramData'
    $package = Join-Path $root 'package'
    foreach ($name in @('artifacts/publish/service/AgentSeat.exe', 'artifacts/publish/cli/agent-seat.exe', 'artifacts/publish/service/rdp-anchor/AgentSeat.RdpAnchor.exe', 'artifacts/publish/service/agent-helper/AgentSeat.AgentHelper.exe', 'artifacts/termwrap/TermWrap.dll')) {
        $path = Join-Path $package $name; [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null; [IO.File]::WriteAllText($path, 'fixture; must never execute')
    }
    [IO.Directory]::CreateDirectory((Join-Path $package 'scripts')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $package 'scripts/Install-MultiSession.ps1'), 'param($TermWrapPath,[switch]$Apply,[switch]$IAcceptUnsupportedWindowsClientPatch) $global:h.PatchCalls++; $global:h.SharedRdp = $true')
    [IO.File]::WriteAllText((Join-Path $package 'scripts/Enable-AgentControl.ps1'), 'param($AllowSeat,$AllowUser,[switch]$Apply) $global:h.AllowCalls++')
    [IO.File]::WriteAllText((Join-Path $package 'release.json'), '{"version":"0.9.2"}')
    $global:h = @{ Package = $package; Port = 3397; Service = $null; Account = $null; Task = $null; Seat = $null; SharedRdp = $false; PatchCalls = 0; AllowCalls = 0; Creates = 0; PasswordHash = $null; TaskRegisters = 0; Stops = 0; Disabled = 0; Fail = ''; BootSession = $null; ServiceStarts = 0; NewPasswordNeverExpires = $false }
    return $package
}
function Test-AgentSeatAdministrator { return $true }
function Get-ItemProperty {
    param($Path, $LiteralPath, $Name, $ErrorAction)
    if (($Path + $LiteralPath) -like '*CurrentVersion') { return @{ EditionID = 'Professional'; CurrentBuildNumber = '26100' } }
    if (($Path + $LiteralPath) -like '*Session Manager*') { return @{ PROCESSOR_ARCHITECTURE = 'AMD64' } }
    if (($Path + $LiteralPath) -like '*RDP-Tcp') { return @{ PortNumber = $global:h.Port } }
    return @{ ServiceDll = $(if ($global:h.SharedRdp) { 'C:\fixture\TermWrap.dll' } else { 'C:\Windows\System32\termsrv.dll' }) }
}
function Get-Service { param($Name, $ErrorAction) if ($Name -eq 'agent-seat') { return $global:h.Service } }
function Get-NetTCPConnection { return $null }
function Get-CimInstance { return @{ PathName = ('"' + (Join-Path $env:ProgramFiles 'agent-seat\app\AgentSeat.exe') + '"') } }
function New-Service {
    param($Name,$DisplayName,$Description,$BinaryPathName,$StartupType)
    $global:h.StartupType = $StartupType
    $global:h.Service = @{ Name = 'agent-seat'; Status = 'Stopped' }
    if ($global:h.Fail -eq 'service-created') { throw 'Simulated process termination after service creation' }
}
function Start-Service { param($Name) $global:h.ServiceStarts++; $global:h.Service.Status = 'Running' }
function Get-LocalUser { param($Name,$ErrorAction) return $global:h.Account }
function New-LocalUser {
    param($Name,$FullName,$Description,$Password,[switch]$AccountNeverExpires,[switch]$PasswordNeverExpires)
    $ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $secret = Join-Path $env:ProgramData "agent-seat\RdpAnchors\$ownerSid\agent\credential.dat"
    if (-not (Test-Path -LiteralPath $secret)) { throw 'Account created before recoverable credentials' }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))
        $global:h.PasswordHash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash($bytes))
        [Array]::Clear($bytes,0,$bytes.Length)
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $global:h.Creates++; $global:h.NewPasswordNeverExpires = $PasswordNeverExpires.IsPresent
    $global:h.Account = [pscustomobject]@{ Name = $Name; Description = $Description; SID = @{ Value = 'S-1-5-21-111-222-333-1001' }; Enabled = $true; PasswordExpires = $null }
    if ($global:h.Fail -eq 'account-created') { throw 'Simulated process termination before saving account SID' }
    return $global:h.Account
}
function Set-LocalUser { param($Name,[bool]$PasswordNeverExpires,$Password) if ($Password) { throw 'Recovery tried to reset a password' }; if (-not $PasswordNeverExpires) { throw 'Password expiry was not repaired' }; $global:h.Account.PasswordExpires = $null }
function Get-LocalGroupMember { param($Group,$ErrorAction) if ($global:h.Member) { return @{ SID = $global:h.Account.SID } } }
function Add-LocalGroupMember { param($Group,$Member) $global:h.Member = $true }
function Get-ScheduledTask { param($TaskName,$ErrorAction) return $global:h.Task }
function Stop-ScheduledTask { param($TaskName) $global:h.Stops++ }
function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) return @{ Execute = $Execute; Arguments = $Argument; WorkingDirectory = $WorkingDirectory } }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) return @{ UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; LogonType = $LogonType; RunLevel = $RunLevel } }
function New-ScheduledTaskSettingsSet { return @{} }
function Register-ScheduledTask {
    param($TaskName,$Action,$Principal,$Settings,[switch]$Force)
    if ($global:h.Fail -eq 'seat-registered') { throw 'Simulated failure registering the anchor task' }
    $global:h.TaskRegisters++; $global:h.Task = @{ Actions = @($Action); Principal = $Principal; Triggers = @(); State = 'Ready' }
}
function Start-ScheduledTask { param($TaskName) $global:h.Task.State = 'Running' }
function Disable-ScheduledTask { param($TaskName) $global:h.Disabled++; $global:h.Task.State = 'Disabled' }
function Wait-AgentSeatSession { param($ApiRoot,$UserName) if ($global:h.Fail -eq 'session-timeout') { throw 'Simulated RDP timeout' }; return @{ sessionId = 8 } }
function Invoke-RestMethod {
    param($Uri,$Method,$Body,$ContentType,$TimeoutSec)
    if ($Uri.EndsWith('/health')) {
        if ($global:h.HealthFailures -gt 0) { $global:h.HealthFailures--; throw 'Simulated service warmup' }
        return @{ mode = 'agent-seat'; version = '0.9.2' }
    }
    if ($Method -in @('Post','Put')) { $global:h.Seat = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json; return $global:h.Seat }
    if ($Uri.EndsWith('/seats') -and $global:h.Seat) { return @{ seat = $global:h.Seat } }
    return @()
}
function Set-AgentSeatAnchorConfiguration {
    param($Executable,$Password,$SeatId,$UserName,$Port,$Width,$Height,$AnchorRoot)
    $dir = Join-Path $AnchorRoot $SeatId
    $config = @{ seatId = $SeatId; server = '127.0.0.2'; domain = $env:COMPUTERNAME; userName = $UserName; port = $Port; width = $Width; height = $Height }
    [IO.File]::WriteAllText((Join-Path $dir 'anchor.json'), ($config | ConvertTo-Json))
    Add-Type -AssemblyName System.Security
    $plain = [Text.Encoding]::Unicode.GetBytes($Password)
    try { $cipher = [Security.Cryptography.ProtectedData]::Protect($plain,[Text.Encoding]::UTF8.GetBytes("AgentSeat/RdpAnchor/$SeatId/v1"),[Security.Cryptography.DataProtectionScope]::CurrentUser); [IO.File]::WriteAllBytes((Join-Path $dir 'credential.dat'), $cipher) }
    finally { [Array]::Clear($plain,0,$plain.Length) }
}
$options = @{ SeatId = 'agent'; UserName = 'agent-seat-user'; DisplayName = 'Fixture desktop'; Width = 1280; Height = 800; Apply = $true; IAcceptUnsupportedWindowsClientPatch = $true }
try {
    Check 'custom listener port reaches the API and anchor; invalid registry ports fail before changes' {
        $package = New-Harness
        Invoke-AgentSeatInstall $package $options
        if ($global:h.Seat.rdpPort -ne 3397) { throw 'Seat API used the wrong port' }
        $ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $config = Get-Content -LiteralPath (Join-Path $env:ProgramData "agent-seat\RdpAnchors\$ownerSid\agent\anchor.json") -Raw | ConvertFrom-Json
        if ($config.port -ne 3397) { throw 'Anchor used the wrong port' }
        foreach ($bad in @(0,65536,'invalid',$null)) {
            $global:h.Port = $bad
            Expect-Failure { Invoke-AgentSeatInstall $package $options }
            if ($global:h.Creates -ne 1 -or $global:h.TaskRegisters -ne 1) { throw 'Invalid port mutated installation' }
        }
    }
    Check 'fresh install saves credentials before creating a non-expiring account and checks completion' {
        $package = New-Harness
        Invoke-AgentSeatInstall $package $options
        $status = Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user'
        if (-not $status.Ready -or -not $status.State.complete -or -not $global:h.NewPasswordNeverExpires) { throw 'Fresh install did not meet readiness requirements' }
        if ($global:h.StartupType -ne 'Automatic' -or $global:h.Task.Principal.LogonType -ne 'Interactive' -or $global:h.Task.Triggers.Count -ne 0 -or $global:h.Task.State -ne 'Disabled') { throw 'Reboot/manual-start contract changed' }
        # A reboot destroys interactive sessions; completed installation must remain reusable.
        $global:h.BootSession = $null
        if (-not (Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'A completed seat became incomplete just because no session is running' }
        $global:h.Seat.rdpPort = 3389
        if ((Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user' $global:h.Seat).Ready) { throw 'A mismatched API port advertised as complete' }
        $global:h.Seat.rdpPort = 3397
        $global:h.Task.Principal.RunLevel = 'Highest'
        if ((Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'An elevated anchor advertised as ready' }
        $global:h.Task.Principal.RunLevel = 'Limited'; $global:h.Task.Triggers = @(@{ AtLogon = $true })
        if ((Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'Automatic seat startup advertised as ready' }
    }
    Check 'failures after service creation, account creation and seat registration resume without a password reset' {
        foreach ($failure in @('service-created','account-created','seat-registered')) {
            $package = New-Harness; $global:h.Fail = $failure
            Expect-Failure { Invoke-AgentSeatInstall $package $options }
            $statePath = Join-Path $env:ProgramData 'agent-seat\Installations\agent\state.json'
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            if ($state.complete) { throw 'Failed installation marked complete' }
            $before = $global:h.PasswordHash; $global:h.Fail = ''; $global:h.HealthFailures = 2
            Invoke-AgentSeatInstall $package $options
            if ($global:h.Creates -ne 1 -or ($before -and $global:h.PasswordHash -ne $before) -or -not (Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw "Resume failed: $failure" }
            if (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'agent-seat\app\service')) { throw 'Retry nested a second service directory' }
        }
    }
    Check 'a timed-out connection leaves a disabled task and resumable incomplete record' {
        $package = New-Harness; $global:h.Fail = 'session-timeout'
        Expect-Failure { Invoke-AgentSeatInstall $package $options }
        if ($global:h.Task.State -ne 'Disabled' -or (Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'Timeout advertised a complete installation or left auto restart enabled' }
        $before = $global:h.PasswordHash; $global:h.Fail = ''
        Invoke-AgentSeatInstall $package $options
        if ($global:h.Creates -ne 1 -or $global:h.PasswordHash -ne $before) { throw 'Timeout recovery changed account identity' }
    }
    Check 'legacy repair preserves usable saved credentials and fixes an expired password and missing task' {
        $package = New-Harness; Invoke-AgentSeatInstall $package $options
        Remove-Item -LiteralPath (Join-Path $env:ProgramData 'agent-seat\Installations\agent\state.json')
        $global:h.Account.PasswordExpires = [DateTime]::UtcNow.AddDays(-1); $global:h.Task = $null
        if ((Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'Broken legacy installation advertised as installed' }
        $before = $global:h.PasswordHash
        Invoke-AgentSeatInstall $package $options
        if ($global:h.Creates -ne 1 -or $global:h.PasswordHash -ne $before -or -not (Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'Legacy repair failed' }
    }
    Check 'unrelated accounts, replaced SIDs and different task owners are refused' {
        $package = New-Harness
        $global:h.Account = [pscustomobject]@{ SID = @{ Value = 'unrelated' }; Enabled = $true; Description = 'unrelated' }
        Expect-Failure { Invoke-AgentSeatInstall $package $options }
        if ($global:h.Creates -or $global:h.TaskRegisters -or $global:h.Service) { throw 'Unrelated account triggered host changes' }
        $package = New-Harness; Invoke-AgentSeatInstall $package $options
        $global:h.Account.SID.Value = 'S-1-5-21-111-222-333-2002'
        Expect-Failure { Invoke-AgentSeatInstall $package $options }
        if ($global:h.TaskRegisters -ne 1) { throw 'Replaced account triggered a repair' }
        $global:h.Account.SID.Value = 'S-1-5-21-111-222-333-1001'; $global:h.Task.Principal.UserId = 'S-1-5-18'
        Expect-Failure { Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user' }
        Expect-Failure { Invoke-AgentSeatInstall $package $options }
        if ($global:h.TaskRegisters -ne 1) { throw 'Foreign task overwritten' }
        $global:h.Task.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $global:h.Account = $null; $global:h.Seat = $null
        Expect-Failure { Invoke-AgentSeatInstall $package $options }
        if ($global:h.Creates -ne 1 -or $global:h.TaskRegisters -ne 1) { throw 'A deleted managed account was recreated' }
    }
    Check 'owner-scoped journals and DPAPI credentials reject a different owner or seat' {
        $package = New-Harness; Invoke-AgentSeatInstall $package $options
        $statePath = Join-Path $env:ProgramData 'agent-seat\Installations\agent\state.json'
        Expect-Failure { Read-AgentSeatInstallation $statePath 'agent' 'agent-seat-user' 'S-1-5-18' }
        $ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $dir = Join-Path $env:ProgramData "agent-seat\RdpAnchors\$ownerSid\agent"
        Expect-Failure { Read-AgentSeatAnchorPassword $dir 'other-seat' 'agent-seat-user' }
        [IO.File]::WriteAllBytes((Join-Path $dir 'credential.dat'), [byte[]]@(1,2,3))
        if ((Get-AgentSeatInstallationStatus 'agent' 'agent-seat-user').Ready) { throw 'Corrupt saved credentials advertised as usable' }
    }
    Check 'a junction in an installation path is rejected before writes' {
        $dir = Join-Path $testRoot 'junction-target'; [IO.Directory]::CreateDirectory($dir) | Out-Null
        $link = Join-Path $testRoot 'junction'
        New-Item -ItemType Junction -Path $link -Target $dir | Out-Null
        Expect-Failure { New-AgentSeatProtectedDirectory (Join-Path $link 'must-not-exist') ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) }
        if (Test-Path -LiteralPath (Join-Path $dir 'must-not-exist')) { throw 'Protected directory followed a junction' }
    }
} finally { $env:ProgramFiles = $originalProgramFiles; $env:ProgramData = $originalProgramData }
Write-Host "Recovery checks passed: $script:passed (Windows PowerShell $($PSVersionTable.PSVersion))"
