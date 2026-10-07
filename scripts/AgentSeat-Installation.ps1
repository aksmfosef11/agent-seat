# Shared installation/recovery functions. Importing this file performs no host changes.
function Get-AgentSeatRdpPort {
    $settings = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' -ErrorAction Stop
    $port = $settings.PortNumber
    if ($null -eq $port -or $port -notmatch '^\d+$' -or [long]$port -lt 1 -or [long]$port -gt 65535) { throw 'The Windows RDP listener port is invalid.' }
    return [int]$port
}

function Test-AgentSeatAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (-not $identity.IsSystem -and [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
}

function Assert-AgentSeatRealPath {
    param([string]$Path)
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation paths must not contain links or junctions.' }
        }
        $current = Split-Path -Parent $current
    }
}

function New-AgentSeatProtectedDirectory {
    param([string]$Path, [string]$OwnerSid)
    Assert-AgentSeatRealPath $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sidText in @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidText)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    # Persist only the DACL. Set-Acl can request SACL privileges when a protected
    # directory is revisited during recovery, even though no audit rules change.
    [IO.Directory]::SetAccessControl($Path, $acl)
}

function Save-AgentSeatInstallation {
    param([string]$Path, $State)
    Assert-AgentSeatRealPath $Path
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Read-AgentSeatInstallation {
    param([string]$Path, [string]$SeatId, [string]$UserName, [string]$OwnerSid)
    Assert-AgentSeatRealPath $Path
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($state.schemaVersion -ne 1 -or $state.seatId -cne $SeatId -or $state.userName -ine $UserName -or $state.ownerSid -ne $OwnerSid -or $state.installId -notmatch '^[0-9a-f]{32}$' -or $state.machineName -ine $env:COMPUTERNAME) { throw 'Installation record belongs to a different seat, owner or computer.' }
    return $state
}

function Read-AgentSeatAnchorPassword {
    param([string]$Directory, [string]$SeatId, [string]$UserName)
    Assert-AgentSeatRealPath $Directory
    $config = Get-Content -LiteralPath (Join-Path $Directory 'anchor.json') -Raw | ConvertFrom-Json
    if ($config.seatId -cne $SeatId -or $config.userName -ine $UserName -or $config.domain -ine $env:COMPUTERNAME -or $config.server -ne '127.0.0.2') { throw 'Saved anchor credentials belong to a different account or host.' }
    Add-Type -AssemblyName System.Security
    $protected = [IO.File]::ReadAllBytes((Join-Path $Directory 'credential.dat'))
    $entropy = [Text.Encoding]::UTF8.GetBytes("AgentSeat/RdpAnchor/$SeatId/v1")
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try { return [Text.Encoding]::Unicode.GetString($plain) }
    finally { [Array]::Clear($plain, 0, $plain.Length) }
}

function Assert-AgentSeatOwnedAccount {
    param($Account, $State)
    if ($State.accountSid) {
        if ($Account.SID.Value -ne $State.accountSid) { throw 'The Windows account was replaced. Its password and settings will not be changed.' }
    } elseif ($Account.Description -cne ('agent-seat:' + $State.installId)) {
        throw 'The existing Windows account was not created by this installation. Its password and settings will not be changed.'
    }
    if (-not $Account.Enabled) { throw 'The seat account is disabled. Review that administrator decision before resuming setup.' }
}

function Test-AgentSeatTaskOwner {
    param($Task, [string]$OwnerSid)
    $taskOwner = $Task.Principal.UserId
    if ($taskOwner -notmatch '^S-1-') { $taskOwner = [Security.Principal.NTAccount]::new($taskOwner).Translate([Security.Principal.SecurityIdentifier]).Value }
    return $taskOwner -eq $OwnerSid
}

function Assert-AgentSeatManagedTask {
    param($Task, [string]$InstallRoot, [string]$OwnerSid)
    if (-not (Test-AgentSeatTaskOwner $Task $OwnerSid)) { throw 'The anchor task belongs to another Windows owner.' }
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { throw 'The anchor task has unexpected actions.' }
    $exe = [IO.Path]::GetFullPath($actions[0].Execute)
    $baseExe = Join-Path $InstallRoot 'app\rdp-anchor\AgentSeat.RdpAnchor.exe'
    $versionedRoot = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'anchors')).TrimEnd('\') + '\'
    if ($exe -ine $baseExe -and (-not $exe.StartsWith($versionedRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($exe) -ine 'AgentSeat.RdpAnchor.exe')) { throw 'The anchor task runs an unrelated executable. Review it manually.' }
    Assert-AgentSeatRealPath $exe
}

function Get-AgentSeatInstallationStatus {
    param([string]$SeatId, [string]$UserName, $Seat = $null)
    $ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $installRoot = Join-Path $env:ProgramFiles 'agent-seat'
    $dataRoot = Join-Path $env:ProgramData 'agent-seat'
    $recordPath = Join-Path $dataRoot "Installations\$SeatId\state.json"
    $directory = Join-Path $dataRoot "RdpAnchors\$ownerSid\$SeatId"
    $issues = [Collections.Generic.List[string]]::new()
    $state = Read-AgentSeatInstallation $recordPath $SeatId $UserName $ownerSid
    $account = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if (-not $account) { $issues.Add('Windows account is missing.') }
    else {
        if ($state) { Assert-AgentSeatOwnedAccount $account $state }
        if (-not $account.Enabled) { $issues.Add('Windows account is disabled.') }
        if ($account.PasswordExpires) { $issues.Add('The saved account password can expire.') }
        $group = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-555')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
        if (-not @(Get-LocalGroupMember -Group $group -ErrorAction Stop | Where-Object { $_.SID.Value -eq $account.SID.Value }).Count) { $issues.Add('Remote Desktop Users membership is missing.') }
    }
    try {
        $password = Read-AgentSeatAnchorPassword $directory $SeatId $UserName
        $password = $null
        $config = Get-Content -LiteralPath (Join-Path $directory 'anchor.json') -Raw | ConvertFrom-Json
        if ([int]$config.port -ne (Get-AgentSeatRdpPort)) { $issues.Add('The saved RDP port differs from the Windows listener.') }
        if ($Seat -and [int]$Seat.rdpPort -ne [int]$config.port) { $issues.Add('The registered seat RDP port differs from its anchor.') }
    } catch { $issues.Add('Saved anchor configuration/credentials are missing or unusable.') }
    $task = Get-ScheduledTask -TaskName "agent-seat RDP Anchor - $SeatId" -ErrorAction SilentlyContinue
    if (-not $task) { $issues.Add('RDP anchor task is missing.') }
    else {
        Assert-AgentSeatManagedTask $task $installRoot $ownerSid
        $actions = @($task.Actions)
        $expectedArguments = 'run --seat {0} --root "{1}"' -f $SeatId, (Split-Path -Parent $directory)
        if ($task.Principal.LogonType -notin @('Interactive', 'InteractiveToken') -or $task.Principal.RunLevel -ne 'Limited' -or @($task.Triggers).Count -ne 0 -or $actions[0].Arguments -cne $expectedArguments -or -not (Test-Path -LiteralPath $actions[0].Execute)) { $issues.Add('RDP anchor task needs repair.') }
    }
    if ($state -and -not $state.complete) { $issues.Add('The recorded installation is incomplete.') }
    return @{ Ready = ($issues.Count -eq 0); Issues = $issues.ToArray(); State = $state }
}
