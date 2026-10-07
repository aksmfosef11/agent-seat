[CmdletBinding()]
param(
    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')][string]$SeatId = 'agent',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$')][string]$UserName = 'agent-seat-user',
    [string]$DisplayName = 'AI Desktop',
    [ValidateRange(640, 3840)][int]$Width = 1280,
    [ValidateRange(480, 2160)][int]$Height = 800,
    [switch]$Apply,
    [switch]$IAcceptUnsupportedWindowsClientPatch
)
. (Join-Path $PSScriptRoot 'scripts\AgentSeat-Installation.ps1')

function Wait-AgentSeatSession {
    param([string]$ApiRoot, [string]$UserName)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        $session = Invoke-RestMethod "$ApiRoot/sessions" -TimeoutSec 5 | Where-Object { $_.userName -ieq $UserName -and $_.state -in @('active', 'connected', 'shadow') } | Select-Object -First 1
        if ($session) { return $session }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'No live seat session after 90 seconds. Re-run setup to resume after inspecting the anchor and first-login screen; see docs/INSTALL.md.'
}

function Wait-AgentSeatHealth {
    param([string]$ApiRoot)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        try { $health = Invoke-RestMethod "$ApiRoot/health" -TimeoutSec 2 }
        catch { $health = $null }
        if ($health) {
            if ($health.mode -ne 'agent-seat') { throw 'Port 38399 belongs to another application.' }
            return $health
        }
        Start-Sleep -Milliseconds 300
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'agent-seat did not expose its health endpoint. Re-run setup to resume.'
}

function Assert-AgentSeatServicePath {
    param([string]$InstallRoot)
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='agent-seat'" -ErrorAction Stop
    $expected = Join-Path $InstallRoot 'app\AgentSeat.exe'
    if (-not $serviceInfo -or $serviceInfo.PathName.Trim('"') -ine $expected) { throw 'The registered agent-seat service uses an unexpected executable. Review it manually.' }
}

function New-AgentSeatPassword {
    $bytes = [byte[]]::new(36)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return [Convert]::ToBase64String($bytes).TrimEnd('=') + '!9a' }
    finally { $rng.Dispose(); [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Set-AgentSeatAnchorConfiguration {
    param([string]$Executable, [string]$Password, [string]$SeatId, [string]$UserName, [int]$Port, [int]$Width, [int]$Height, [string]$AnchorRoot)
    $Password | & $Executable configure --seat $SeatId --server 127.0.0.2 --port $Port --domain $env:COMPUTERNAME --user $UserName --width $Width --height $Height --root $AnchorRoot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Anchor configuration failed. Re-run setup to resume.' }
}

function Invoke-AgentSeatInstall {
    param([string]$Root, [hashtable]$Options)
    $ErrorActionPreference = 'Stop'
    $serviceName = 'agent-seat'
    $installRoot = Join-Path $env:ProgramFiles 'agent-seat'
    $dataRoot = Join-Path $env:ProgramData 'agent-seat'
    $published = Join-Path $Root 'artifacts\publish'
    $termWrap = Join-Path $Root 'artifacts\termwrap\TermWrap.dll'
    $apiRoot = 'http://127.0.0.1:38399/api/v1'
    $seatId = $Options.SeatId; $userName = $Options.UserName
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent()
    $ownerSid = $owner.User.Value
    $taskName = "agent-seat RDP Anchor - $seatId"
    $anchorRoot = Join-Path $dataRoot "RdpAnchors\$ownerSid"
    $anchorDirectory = Join-Path $anchorRoot $seatId
    $stateDirectory = Join-Path $dataRoot "Installations\$seatId"
    $statePath = Join-Path $stateDirectory 'state.json'
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $architecture = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or $architecture -ne 'AMD64' -or $windows.EditionID -notmatch '^(Professional|Enterprise|Education)' -or [int]$windows.CurrentBuildNumber -lt 22000) { throw 'Use 64-bit PowerShell on Windows 11 Pro/Enterprise/Education x64.' }
    foreach ($required in @('service\AgentSeat.exe', 'service\rdp-anchor\AgentSeat.RdpAnchor.exe', 'service\agent-helper\AgentSeat.AgentHelper.exe', 'cli\agent-seat.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing $required. Download the executable release ZIP." }
    }
    if (-not (Test-Path -LiteralPath $termWrap)) { throw 'TermWrap.dll is missing from the release package.' }
    $manifestPath = Join-Path $Root 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
        foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
            $path = [IO.Path]::GetFullPath((Join-Path $Root $entry.path))
            if ($entry.path.Contains(':') -or -not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Package verification failed: $($entry.path)" }
        }
    }
    $rdpPort = Get-AgentSeatRdpPort
    $state = Read-AgentSeatInstallation $statePath $seatId $userName $ownerSid
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $dll = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters' -Name ServiceDll -ErrorAction SilentlyContinue).ServiceDll
    $sharedRdp = [IO.Path]::GetFileName([Environment]::ExpandEnvironmentVariables([string]$dll)) -ieq 'TermWrap.dll'
    Write-Host 'agent-seat installation plan'
    Write-Host "  Service: $serviceName; app/CLI: $installRoot; data: $dataRoot"
    Write-Host "  UI/API: http://127.0.0.1:38399; account: $userName; seat: $seatId; RDP port: $rdpPort"
    Write-Host "  Existing TermWrap: $sharedRdp (reused without restarting TermService)"
    Write-Host "  Saved installation: $([bool]$state); account passwords are preserved when resuming."
    Write-Host '  After reboot: log in as the installing owner and start the seat from CLI or viewer.'
    if (-not $Options.Apply) { Write-Host 'Dry run. Add -Apply -IAcceptUnsupportedWindowsClientPatch to install or repair.'; return }
    if (-not $Options.IAcceptUnsupportedWindowsClientPatch) { throw 'Explicit -IAcceptUnsupportedWindowsClientPatch is required.' }
    if (-not (Test-AgentSeatAdministrator)) { throw 'Run in Administrator PowerShell as the interactive Windows owner.' }
    $mutex = [Threading.Mutex]::new($false, 'Global\AgentSeat_Installation')
    $held = $false
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Another agent-seat installation is running.' }
        $state = Read-AgentSeatInstallation $statePath $seatId $userName $ownerSid
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $account = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
        $existingSeat = $null
        if ($service) {
            Assert-AgentSeatServicePath $installRoot
            if ($service.Status -ne 'Running') { Start-Service $serviceName }
            $health = Wait-AgentSeatHealth $apiRoot
            $views = @(Invoke-RestMethod "$apiRoot/seats" -TimeoutSec 5)
            $matchingSeats = @($views | Where-Object { $_.seat.id -ceq $seatId } | ForEach-Object { $_.seat })
            if ($matchingSeats.Count -gt 1) { throw 'Duplicate seat identity.' }
            $existingSeat = $matchingSeats | Select-Object -First 1
            if ($existingSeat -and $existingSeat.userName -ine $userName) { throw 'Seat ID belongs to a different Windows account.' }
            if ($views | Where-Object { $_.seat.id -cne $seatId -and $_.seat.userName -ieq $userName }) { throw 'Windows account belongs to a different seat.' }
        } else {
            if ((Test-Path -LiteralPath $installRoot) -and (-not $state -or -not $state.serviceOwned)) { throw 'The app folder has no registered service or matching installation record. Retained for inspection.' }
            if (Get-NetTCPConnection -State Listen -LocalPort 38399 -ErrorAction SilentlyContinue) { throw 'Port 38399 is already in use.' }
        }
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task) { Assert-AgentSeatManagedTask $task $installRoot $ownerSid }
        if ($account -and -not $state) {
            if (-not $existingSeat) { throw 'The existing Windows account has no matching managed seat. Choose a new account name.' }
            if (-not $account.Enabled) { throw 'The seat account is disabled. Review that administrator decision before repairing.' }
            $savedPassword = Read-AgentSeatAnchorPassword $anchorDirectory $seatId $userName
            $savedPassword = $null
        }
        if ($state -and $account) { Assert-AgentSeatOwnedAccount $account $state }
        if ($state -and $state.accountSid -and -not $account) { throw 'The recorded Windows account is missing. Review the deleted account before repairing.' }
        if ($existingSeat -and -not $account) { throw 'The registered seat account is missing. Review the deleted account before repairing.' }
        if (-not $state) {
            $state = [pscustomobject]@{ schemaVersion = 1; installId = [Guid]::NewGuid().ToString('N'); machineName = $env:COMPUTERNAME; ownerSid = $ownerSid; seatId = $seatId; userName = $userName; accountSid = $(if ($account) { $account.SID.Value } else { $null }); serviceOwned = (-not [bool]$service); complete = $false; displayName = $Options.DisplayName; width = $Options.Width; height = $Options.Height }
            New-AgentSeatProtectedDirectory $stateDirectory $ownerSid
            Save-AgentSeatInstallation $statePath $state
        }
        $state.complete = $false
        Save-AgentSeatInstallation $statePath $state
        if (-not $sharedRdp) {
            if (Get-Service -Name SeatStream -ErrorAction SilentlyContinue) { throw 'An existing desktop service has an unrecognized RDP configuration. Refusing to change shared RDP.' }
            & (Join-Path $Root 'scripts\Install-MultiSession.ps1') -TermWrapPath $termWrap -Apply -IAcceptUnsupportedWindowsClientPatch
        }
        Assert-AgentSeatRealPath $installRoot
        if (-not $service) {
            [IO.Directory]::CreateDirectory((Join-Path $installRoot 'app')) | Out-Null
            [IO.Directory]::CreateDirectory((Join-Path $installRoot 'cli')) | Out-Null
            Copy-Item -Path (Join-Path $published 'service\*') -Destination (Join-Path $installRoot 'app') -Recurse -Force
            Copy-Item -Path (Join-Path $published 'cli\*') -Destination (Join-Path $installRoot 'cli') -Recurse -Force
            New-Service -Name $serviceName -DisplayName 'agent-seat' -Description 'Separate Windows desktops for AI with local screen and input control' -BinaryPathName ('"{0}"' -f (Join-Path $installRoot 'app\AgentSeat.exe')) -StartupType Automatic | Out-Null
            Start-Service $serviceName
            $health = Wait-AgentSeatHealth $apiRoot
        }
        $version = (Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw | ConvertFrom-Json).version
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
        $anchorPublish = Join-Path $published 'service\rdp-anchor'
        $anchorInstall = Join-Path $installRoot "anchors\$version"
        Assert-AgentSeatRealPath $anchorInstall
        if ($task) { Stop-ScheduledTask -TaskName $taskName }
        [IO.Directory]::CreateDirectory($anchorInstall) | Out-Null
        $copyNeeded = $false
        foreach ($sourceFile in Get-ChildItem -LiteralPath $anchorPublish -Recurse -File) {
            $target = Join-Path $anchorInstall $sourceFile.FullName.Substring($anchorPublish.Length + 1)
            if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $sourceFile.FullName).Hash) { $copyNeeded = $true; break }
        }
        if ($copyNeeded) { Copy-Item -Path (Join-Path $anchorPublish '*') -Destination $anchorInstall -Recurse -Force }
        $anchor = Join-Path $anchorInstall 'AgentSeat.RdpAnchor.exe'
        New-AgentSeatProtectedDirectory $anchorRoot $ownerSid
        New-AgentSeatProtectedDirectory $anchorDirectory $ownerSid
        if (Test-Path -LiteralPath (Join-Path $anchorDirectory 'credential.dat')) { $password = Read-AgentSeatAnchorPassword $anchorDirectory $seatId $userName }
        elseif ($account) { throw 'Saved credentials are missing. The existing account password will not be reset.' }
        else { $password = New-AgentSeatPassword }
        $width = if ($existingSeat) { [int]$existingSeat.width } else { [int]$state.width }
        $height = if ($existingSeat) { [int]$existingSeat.height } else { [int]$state.height }
        try {
            Set-AgentSeatAnchorConfiguration $anchor $password $seatId $userName $rdpPort $width $height $anchorRoot
            if (-not $account) {
                $account = New-LocalUser -Name $userName -FullName ("agent-seat " + $state.displayName) -Description ('agent-seat:' + $state.installId) -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires
                $state.accountSid = $account.SID.Value
                Save-AgentSeatInstallation $statePath $state
            } else {
                Assert-AgentSeatOwnedAccount $account $state
                Set-LocalUser -Name $userName -PasswordNeverExpires $true
                $state.accountSid = $account.SID.Value
                Save-AgentSeatInstallation $statePath $state
            }
        } finally { $password = $null }
        $rdpGroup = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-555')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
        if (-not @(Get-LocalGroupMember -Group $rdpGroup | Where-Object { $_.SID.Value -eq $account.SID.Value }).Count) { Add-LocalGroupMember -Group $rdpGroup -Member $userName }
        & (Join-Path $Root 'scripts\Enable-AgentControl.ps1') -AllowSeat $seatId -AllowUser $userName -Apply
        if ($existingSeat) {
            $body = $existingSeat
            $body.rdpPort = $rdpPort
            Invoke-RestMethod -Method Put -Uri "$apiRoot/seats/$seatId" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json))) -TimeoutSec 30 | Out-Null
        } else {
            $body = @{ id = $seatId; displayName = $state.displayName; userName = $userName; hostAddress = '127.0.0.2'; rdpPort = $rdpPort; width = $width; height = $height; fullScreen = $false; playAudioOnClient = $false; redirectClipboard = $false; streamingEnabled = $false; autoStartStreaming = $false; agentControlEnabled = $true; sunshineBasePort = 0; enabled = $true }
            Invoke-RestMethod -Method Post -Uri "$apiRoot/seats" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json))) -TimeoutSec 30 | Out-Null
        }
        $action = New-ScheduledTaskAction -Execute $anchor -Argument ('run --seat {0} --root "{1}"' -f $seatId, $anchorRoot) -WorkingDirectory $anchorInstall
        $principal = New-ScheduledTaskPrincipal -UserId $owner.Name -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        try { Start-ScheduledTask -TaskName $taskName; $session = Wait-AgentSeatSession $apiRoot $userName }
        finally { Disable-ScheduledTask -TaskName $taskName | Out-Null }
        $state.complete = $true
        Save-AgentSeatInstallation $statePath $state
        @{ service = $serviceName; seat = $seatId; windowsUser = $userName; ownerSid = $ownerSid; accountSid = $account.SID.Value; sharedRdpWasPresent = $sharedRdp; rdpPort = $rdpPort; sessionId = $session.sessionId } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot "installation-$seatId.json") -Encoding UTF8
        Write-Host "Installed agent-seat. Open the screen with: & '$installRoot\cli\agent-seat.exe' computer view --seat $seatId"
    } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-AgentSeatInstall $PSScriptRoot @{ SeatId = $SeatId; UserName = $UserName; DisplayName = $DisplayName; Width = $Width; Height = $Height; Apply = [bool]$Apply; IAcceptUnsupportedWindowsClientPatch = [bool]$IAcceptUnsupportedWindowsClientPatch }
}
