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
$ErrorActionPreference = 'Stop'
$serviceName = 'agent-seat'
$installRoot = Join-Path $env:ProgramFiles 'agent-seat'
$dataRoot = Join-Path $env:ProgramData 'agent-seat'
$published = Join-Path $PSScriptRoot 'artifacts\publish'
$termWrap = Join-Path $PSScriptRoot 'artifacts\termwrap\TermWrap.dll'
$apiRoot = 'http://127.0.0.1:38399/api/v1'
$taskName = "agent-seat RDP Anchor - $SeatId"
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Use 64-bit PowerShell on Windows x64.' }
if ($edition -notmatch '^(Professional|Enterprise|Education)') { throw "This installer targets Windows Pro/Enterprise/Education x64, not '$edition'." }
foreach ($required in @('service\AgentSeat.exe', 'service\rdp-anchor\AgentSeat.RdpAnchor.exe', 'service\agent-helper\AgentSeat.AgentHelper.exe', 'cli\agent-seat.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Missing $required. Download the release ZIP, or build with scripts\Package-Release.ps1." }
}
if (-not (Test-Path -LiteralPath $termWrap)) { throw 'TermWrap.dll is missing from the release package.' }
# Verify the package's declared file set before any host changes. Authenticity is provided by the separately published ZIP checksum.
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
if (Test-Path -LiteralPath $manifestPath) {
    $prefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
        $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.path))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.sha256) { throw "Package verification failed: $($entry.path)" }
    }
}
$legacy = Get-Service -Name SeatStream -ErrorAction SilentlyContinue
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$dllValue = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\TermService\Parameters' -Name ServiceDll -ErrorAction SilentlyContinue).ServiceDll
$sharedRdp = [IO.Path]::GetFileName([Environment]::ExpandEnvironmentVariables([string]$dllValue)) -ieq 'TermWrap.dll'
Write-Host 'agent-seat installation plan'
Write-Host "  Service: $serviceName; app/CLI: $installRoot; data: $dataRoot"
Write-Host "  UI/API: http://127.0.0.1:38399; account: $UserName; seat: $SeatId; display: ${Width}x${Height}"
Write-Host "  Existing SeatStream: $([bool]$legacy); existing TermWrap: $sharedRdp (reused without restarting TermService)"
Write-Host '  Sunshine, gaming devices and existing SeatStream settings are not installed or updated.'
if (-not $Apply) { Write-Host 'Dry run. Add -Apply -IAcceptUnsupportedWindowsClientPatch to install.'; return }
if (-not $IAcceptUnsupportedWindowsClientPatch) { throw 'Explicit -IAcceptUnsupportedWindowsClientPatch is required.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or $owner -match 'SYSTEM$') { throw 'Run in Administrator PowerShell as the interactive Windows owner.' }
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { throw "Account '$UserName' already exists. Choose a new -SeatId and -UserName. Existing passwords are never reset." }
if ($service) {
    $existingHealth = Invoke-RestMethod "$apiRoot/health" -TimeoutSec 5
    if ($existingHealth.mode -ne 'agent-seat') { throw 'Port 38399 belongs to another application.' }
    $existingSeats = Invoke-RestMethod "$apiRoot/seats" -TimeoutSec 5
    if ($existingSeats | Where-Object { $_.seat.id -eq $SeatId -or $_.seat.userName -ieq $UserName }) { throw 'Seat ID or Windows user is already configured.' }
} else {
    if (Test-Path -LiteralPath $installRoot) { throw "'$installRoot' exists without a registered service. Retained for inspection." }
    if (Get-NetTCPConnection -State Listen -LocalPort 38399 -ErrorAction SilentlyContinue) { throw 'Port 38399 is already in use.' }
}
if (-not $sharedRdp) {
    if ($legacy) { throw 'SeatStream is installed without recognizable TermWrap. Refusing to change the shared RDP service; review it manually.' }
    & (Join-Path $PSScriptRoot 'scripts\Install-MultiSession.ps1') -TermWrapPath $termWrap -Apply -IAcceptUnsupportedWindowsClientPatch
}
if (-not $service) {
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $published 'service') -Destination (Join-Path $installRoot 'app') -Recurse
    Copy-Item -LiteralPath (Join-Path $published 'cli') -Destination (Join-Path $installRoot 'cli') -Recurse
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    New-Service -Name $serviceName -DisplayName 'agent-seat' -Description 'Separate Windows desktops for AI with local screen and input control' -BinaryPathName ('"{0}"' -f (Join-Path $installRoot 'app\AgentSeat.exe')) -StartupType Automatic | Out-Null
    Start-Service $serviceName
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do { try { $health = Invoke-RestMethod "$apiRoot/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 300 } } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $health -or $health.mode -ne 'agent-seat') { throw 'agent-seat did not expose its health endpoint.' }
}
$bytes = [byte[]]::new(36); $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
$password = [Convert]::ToBase64String($bytes).TrimEnd('=') + '!9a'
[Array]::Clear($bytes, 0, $bytes.Length)
New-LocalUser -Name $UserName -FullName "agent-seat $DisplayName" -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires | Out-Null
$rdpGroup = ([Security.Principal.SecurityIdentifier]::new('S-1-5-32-555')).Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
Add-LocalGroupMember -Group $rdpGroup -Member $UserName
& (Join-Path $PSScriptRoot 'scripts\Enable-AgentControl.ps1') -AllowSeat $SeatId -AllowUser $UserName -Apply
$body = @{ id = $SeatId; displayName = $DisplayName; userName = $UserName; hostAddress = '127.0.0.2'; rdpPort = 3389; width = $Width; height = $Height; fullScreen = $false; playAudioOnClient = $false; redirectClipboard = $false; streamingEnabled = $false; autoStartStreaming = $false; agentControlEnabled = $true; sunshineBasePort = 0; enabled = $true }
Invoke-RestMethod -Method Post -Uri "$apiRoot/seats" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json))) -TimeoutSec 30 | Out-Null
$anchor = Join-Path $installRoot 'app\rdp-anchor\AgentSeat.RdpAnchor.exe'
$anchorRoot = Join-Path $dataRoot ('RdpAnchors\' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
New-Item -ItemType Directory -Path $anchorRoot -Force | Out-Null
$anchorAcl = [Security.AccessControl.DirectorySecurity]::new()
$anchorAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $anchorAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $anchorRoot -AclObject $anchorAcl
$password | & $anchor configure --seat $SeatId --server 127.0.0.2 --port 3389 --domain $env:COMPUTERNAME --user $UserName --width $Width --height $Height --root $anchorRoot | Out-Null
$password = $null
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure the hidden RDP anchor.' }
$taskAction = New-ScheduledTaskAction -Execute $anchor -Argument ('run --seat {0} --root "{1}"' -f $SeatId, $anchorRoot) -WorkingDirectory (Split-Path -Parent $anchor)
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $owner -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(90); $session = $null
do {
    $session = Invoke-RestMethod "$apiRoot/sessions" -TimeoutSec 5 | Where-Object { $_.userName -ieq $UserName -and $_.state -in @('active', 'connected', 'shadow') } | Select-Object -First 1
    if ($session) { break }; Start-Sleep -Milliseconds 500
} while ([DateTimeOffset]::UtcNow -lt $deadline)
Disable-ScheduledTask -TaskName $taskName | Out-Null
if (-not $session) { throw 'No live seat session after 90 seconds. Inspect the anchor status and complete first-login setup through RDP if needed; see docs/INSTALL.md.' }
@{ service = $serviceName; seat = $SeatId; windowsUser = $UserName; sharedRdpWasPresent = $sharedRdp; sessionId = $session.sessionId } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot "installation-$SeatId.json") -Encoding UTF8
Write-Host "Installed agent-seat. Open the screen with: & '$installRoot\cli\agent-seat.exe' computer view --seat $SeatId"
