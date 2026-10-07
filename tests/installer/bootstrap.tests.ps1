# Offline tests. No downloads, UAC prompts, services or Windows accounts are changed.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'Get-AgentSeat.ps1')
. (Join-Path $repoRoot 'Setup-Seat.ps1')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$testRoot = Join-Path $repoRoot ('artifacts\installer-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:passed = 0
function Check([string]$Name, [scriptblock]$Body) { & $Body; $script:passed++; Write-Host "PASS $Name" }
function Expect-Failure([scriptblock]$Body) {
    $failed = $false
    try { & $Body | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'Expected validation to reject the input.' }
}
function New-FixtureArchive([string]$Name, [string[]]$Entries) {
    $path = Join-Path $testRoot ($Name + '.zip')
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries) {
            $entry = $archive.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('fixture') } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    return $path
}
$zip = New-FixtureArchive 'good' @('nested/file.txt', 'hello.txt')
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$release = @{ tag_name = 'v0.9.0'; draft = $false; assets = @(
    @{ name = 'agent-seat-0.9.0-win-x64.zip'; browser_download_url = 'https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.0/agent-seat-0.9.0-win-x64.zip'; state = 'uploaded'; size = (Get-Item -LiteralPath $zip).Length; digest = "sha256:$hash" },
    @{ name = 'SHA256SUMS.txt'; browser_download_url = 'https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.0/SHA256SUMS.txt' }
) }
Check 'release assets are pinned to the exact public repository/tag' {
    $script:asset = Get-AgentSeatReleaseAsset $release '0.9.0'
    if ($asset.Name -ne 'agent-seat-0.9.0-win-x64.zip') { throw 'Wrong ZIP' }
    Expect-Failure { Get-AgentSeatReleaseAsset $release '0.8.0' }
    $release.assets[0].browser_download_url = 'https://example.com/installer.zip'
    Expect-Failure { Get-AgentSeatReleaseAsset $release '0.9.0' }
    $release.assets[0].browser_download_url = 'https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.0/agent-seat-0.9.0-win-x64.zip'
}
Check 'checksum parser accepts the intended ZIP and ignores other assets' {
    Assert-AgentSeatDownload $zip ("$hash  $($asset.Name)`r`n$hash  Install-AgentSeat.cmd`r`n") $asset
}
Check 'tampered bytes, conflicting checksums, metadata hashes and missing assets fail closed' {
    Expect-Failure { Assert-AgentSeatDownload $zip ('0' * 64 + '  ' + $asset.Name) $asset }
    Expect-Failure { Assert-AgentSeatDownload $zip ("$hash  $($asset.Name)`n$hash  $($asset.Name)") $asset }
    $asset.Zip.digest = 'sha256:' + ('0' * 64)
    Expect-Failure { Assert-AgentSeatDownload $zip ("$hash  $($asset.Name)") $asset }
    $asset.Zip.digest = "sha256:$hash"
    Expect-Failure { Get-AgentSeatReleaseAsset @{ tag_name = 'v0.9.0'; assets = @() } '0.9.0' }
}
Check 'normal archives extract into a new directory' {
    $destination = Join-Path $testRoot 'expanded-good'
    Expand-AgentSeatArchive $zip $destination
    if ((Get-Content -LiteralPath (Join-Path $destination 'nested/file.txt') -Raw) -ne 'fixture') { throw 'Missing extracted content' }
    Expect-Failure { Expand-AgentSeatArchive $zip $destination }
}
Check 'traversal, absolute paths, ADS, Windows aliases and duplicate names are rejected before writing' {
    $cases = @(@('ok.txt', '../outside.txt'), @('/absolute.txt'), @('C:\outside.txt'), @('file.txt:stream'), @('folder./file.txt'), @('CON.txt'), @('same.txt', 'SAME.txt'))
    $number = 0
    foreach ($paths in $cases) {
        $number++; $badZip = New-FixtureArchive "unsafe-$number" $paths; $target = Join-Path $testRoot "expanded-unsafe-$number"
        Expect-Failure { Expand-AgentSeatArchive $badZip $target }
        if (Test-Path -LiteralPath $target) { throw 'Unsafe archive wrote a destination' }
    }
}
Check 'package identity, complete manifest and tamper checks protect installer execution' {
    $package = Join-Path $testRoot 'package'
    New-Item -ItemType Directory -Path $package | Out-Null
    foreach ($name in @('Get-AgentSeat.ps1', 'Install.ps1', 'Setup-Seat.ps1', 'Install-AgentSeat.cmd', 'artifacts/publish/service/AgentSeat.exe', 'artifacts/publish/cli/agent-seat.exe')) {
        $path = Join-Path $package $name; New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'fixture')
    }
    [IO.File]::WriteAllText((Join-Path $package 'release.json'), '{"product":"agent-seat","version":"0.9.0","runtime":"win-x64","selfContained":true}')
    $manifest = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { @{ path = $_.FullName.Substring($package.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    [IO.File]::WriteAllText((Join-Path $package 'manifest.json'), ($manifest | ConvertTo-Json))
    Assert-AgentSeatPackage $package '0.9.0'
    Expect-Failure { Assert-AgentSeatPackage $package '0.8.0' }
    [IO.File]::WriteAllText((Join-Path $package 'undeclared.txt'), 'fixture')
    Expect-Failure { Assert-AgentSeatPackage $package '0.9.0' }
    Remove-Item -LiteralPath (Join-Path $package 'undeclared.txt')
    [IO.File]::WriteAllText((Join-Path $package 'Install.ps1'), 'tampered')
    Expect-Failure { Assert-AgentSeatPackage $package '0.9.0' }
}
Check 'elevation preserves arbitrary display names as data rather than code' {
    $captureScript = Join-Path $testRoot "capture with ' quote.ps1"
    [IO.File]::WriteAllText($captureScript, 'param($DisplayName, [switch]$NoOpen) [Console]::Write($DisplayName)')
    $display = 'AI '' quote " $([Console]::Write("INJECTED")) ` name; & echo wrong'
    $encoded = New-AgentSeatElevationCommand $captureScript @{ DisplayName = $display; NoOpen = $true }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $output = & $ps -NoProfile -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -cne $display) { throw 'Argument escaping changed data or executed it' }
}
function New-SetupFixture {
    $package = Join-Path $testRoot ('setup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $package | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Get-AgentSeat.ps1'), (Join-Path $repoRoot 'Setup-Seat.ps1'), (Join-Path $repoRoot 'Install-AgentSeat.cmd') -Destination $package
    [IO.File]::WriteAllText((Join-Path $package 'Install.ps1'), 'param($SeatId,$UserName,$DisplayName,[switch]$Apply) if ($Apply) { throw "Install must not execute" }')
    foreach ($name in @('artifacts/publish/service/AgentSeat.exe', 'artifacts/publish/cli/agent-seat.exe')) {
        $path = Join-Path $package $name; New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null; [IO.File]::WriteAllText($path, 'fixture')
    }
    [IO.File]::WriteAllText((Join-Path $package 'release.json'), '{"product":"agent-seat","version":"0.9.0","runtime":"win-x64","selfContained":true}')
    $manifest = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { @{ path = $_.FullName.Substring($package.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    [IO.File]::WriteAllText((Join-Path $package 'manifest.json'), ($manifest | ConvertTo-Json))
    return $package
}
Check 'cancelled consent performs neither elevation nor installation' {
    function Get-Service { return $null }
    function Read-Host { return 'cancel' }
    function Start-Process { throw 'Unexpected elevation' }
    $fixture = New-SetupFixture
    Invoke-AgentSeatSetup $fixture (Join-Path $fixture 'Setup-Seat.ps1') @{ SeatId = 'agent'; UserName = 'agent-seat-user'; DisplayName = 'AI Desktop'; Language = 'en'; NoOpen = $true }
}
Check 'a repeated installation keeps the existing seat and skips installer/elevation' {
    function Get-Service { return @{ Name = 'agent-seat' } }
    function Read-Host { throw 'Unexpected consent prompt' }
    function Start-Process { throw 'Unexpected elevation' }
    function Invoke-RestMethod {
        param([string]$Uri, $TimeoutSec)
        if ($Uri.EndsWith('/health')) { return @{ mode = 'agent-seat'; version = '0.7.0' } }
        return @(@{ seat = @{ id = 'agent'; userName = 'agent-seat-user' } })
    }
    $fixture = New-SetupFixture
    Invoke-AgentSeatSetup $fixture (Join-Path $fixture 'Setup-Seat.ps1') @{ SeatId = 'agent'; UserName = 'agent-seat-user'; DisplayName = 'AI Desktop'; Language = 'ko'; NoOpen = $true }
    Expect-Failure { Invoke-AgentSeatSetup $fixture (Join-Path $fixture 'Setup-Seat.ps1') @{ SeatId = 'agent'; UserName = 'other-user'; Language = 'en'; NoOpen = $true } }
}
Check 'UAC under another Windows owner is rejected before package/host actions' {
    Expect-Failure { Invoke-AgentSeatSetup 'unused' 'unused' @{ Elevated = $true; ExpectedOwnerSid = 'wrong'; ExpectedSessionId = -1 } }
}
Write-Host "Installer checks passed: $script:passed (Windows PowerShell $($PSVersionTable.PSVersion))"
