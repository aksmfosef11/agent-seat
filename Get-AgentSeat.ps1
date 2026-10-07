# Public bootstrap: downloads only the requested release from aksmfosef11/agent-seat.
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version = '0.9.0',
    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')][string]$SeatId = 'agent',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$')][string]$UserName = 'agent-seat-user',
    [string]$DisplayName = 'AI Desktop',
    [ValidateSet('auto', 'en', 'ko', 'zh')][string]$Language = 'auto',
    [switch]$DownloadOnly,
    [switch]$Plan,
    [switch]$NoOpen,
    [switch]$IAcceptUnsupportedWindowsClientPatch
)

function Get-AgentSeatReleaseAsset {
    param($Release, [string]$Version)
    $name = "agent-seat-$Version-win-x64.zip"
    $base = "https://github.com/aksmfosef11/agent-seat/releases/download/v$Version/"
    if ($Release.draft -or $Release.tag_name -cne "v$Version") { throw 'Unexpected or unpublished release.' }
    $assets = @($Release.assets | Where-Object { $_.name -ceq $name })
    $checksums = @($Release.assets | Where-Object { $_.name -ceq 'SHA256SUMS.txt' })
    if ($assets.Count -ne 1 -or $checksums.Count -ne 1) { throw 'Release ZIP/checksum assets are missing or ambiguous.' }
    if ($assets[0].browser_download_url -cne ($base + $name) -or $checksums[0].browser_download_url -cne ($base + 'SHA256SUMS.txt')) { throw 'Unexpected release download origin.' }
    if ($assets[0].state -ne 'uploaded' -or $assets[0].size -le 0 -or $assets[0].size -gt 2GB -or $assets[0].digest -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw 'Release size/digest metadata is invalid.' }
    return @{ Name = $name; Zip = $assets[0]; Checksums = $checksums[0] }
}

function Assert-AgentSeatDownload {
    param([string]$ZipPath, [string]$ChecksumText, $Asset)
    $pattern = '^([a-fA-F0-9]{64})\s+\*?' + [regex]::Escape($Asset.Name) + '$'
    $lines = @($ChecksumText -split '\r?\n' | Where-Object { $_.Trim() -match $pattern })
    if ($lines.Count -ne 1) { throw 'A unique ZIP checksum is required.' }
    $null = $lines[0].Trim() -match $pattern
    $expected = $Matches[1]
    $actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    if ((Get-Item -LiteralPath $ZipPath).Length -ne $Asset.Zip.size -or $actual -ine $expected -or $Asset.Zip.digest -ine "sha256:$actual") { throw 'Release verification failed. No installer will be executed.' }
}

function Expand-AgentSeatArchive {
    param([string]$ZipPath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $prefix = $root + '\'
    if (Test-Path -LiteralPath $root) { throw 'Extraction requires a new directory.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $entries = @(); [long]$total = 0
        if ($archive.Entries.Count -gt 10000) { throw 'Too many archive entries.' }
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            $parts = $relative.TrimEnd('\').Split('\')
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($parts | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$|^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)' })) { throw 'Unsafe archive path.' }
            $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
            if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $targets.Add($path)) { throw 'Unsafe or duplicate archive path.' }
            # Refuse Unix symlinks; do not materialize links or Windows alternate data streams.
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive links are unsupported.' }
            $total += $entry.Length
            if ($entry.Length -gt 512MB -or $total -gt 4GB) { throw 'Archive exceeds extraction limits.' }
            $entries += @{ Entry = $entry; Path = $path; Directory = $relative.EndsWith('\') }
        }
        # Validate the entire path set before creating or writing any extracted file.
        New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
        foreach ($item in $entries) {
            if ($item.Directory) { New-Item -ItemType Directory -Path $item.Path -Force | Out-Null }
            else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $item.Path) -Force | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($item.Entry, $item.Path, $false)
            }
        }
    } finally { $archive.Dispose() }
}

function Assert-AgentSeatPackage {
    param([string]$Root, [string]$Version)
    $metadata = Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw | ConvertFrom-Json
    if ($metadata.product -cne 'agent-seat' -or $metadata.version -cne $Version -or $metadata.runtime -cne 'win-x64' -or -not $metadata.selfContained) { throw 'Unexpected release package.' }
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw | ConvertFrom-Json
    if (-not $manifest) { throw 'Package manifest is empty.' }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest) {
        $path = [IO.Path]::GetFullPath((Join-Path $Root $entry.path))
        if ($entry.path.Contains(':') -or -not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not $paths.Add($path) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid package manifest.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Package integrity check failed: $($entry.path)" }
    }
    foreach ($required in @('Get-AgentSeat.ps1', 'Install.ps1', 'Setup-Seat.ps1', 'Install-AgentSeat.cmd', 'release.json', 'artifacts/publish/service/AgentSeat.exe', 'artifacts/publish/cli/agent-seat.exe')) {
        if (-not $paths.Contains([IO.Path]::GetFullPath((Join-Path $Root $required)))) { throw "Missing package file: $required" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse) {
        if ($file.FullName -ine (Join-Path $Root 'manifest.json') -and -not $paths.Contains($file.FullName)) { throw 'Package contains an undeclared file.' }
    }
}

function Invoke-AgentSeatBootstrap {
    param([string]$Version, [string]$SeatId, [string]$UserName, [string]$DisplayName, [string]$Language,
        [switch]$DownloadOnly, [switch]$Plan, [switch]$NoOpen, [switch]$IAcceptUnsupportedWindowsClientPatch)
    $ErrorActionPreference = 'Stop'
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'Use 64-bit PowerShell on Windows x64.' }
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $nativeArchitecture = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
    if ($windows.EditionID -notmatch '^(Professional|Enterprise|Education)' -or [int]$windows.CurrentBuildNumber -lt 22000 -or $nativeArchitecture -ne 'AMD64') { throw 'Windows 11 Pro/Enterprise/Education x64 is required.' }
    $locale = if ($Language -eq 'auto') { [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName } else { $Language }
    $text = switch ($locale) {
        'ko' { @{ download = 'agent-seat {0} 다운로드 및 검증 중…'; verified = '검증 완료: {0}'; saved = '다운로드만 완료했습니다. 설치하려면 폴더 안의 Install-AgentSeat.cmd를 실행하세요.' } }
        'zh' { @{ download = '正在下载并验证 agent-seat {0}…'; verified = '验证完成：{0}'; saved = '仅完成下载。安装请运行文件夹中的 Install-AgentSeat.cmd。' } }
        default { @{ download = 'Downloading and verifying agent-seat {0}…'; verified = 'Verified package: {0}'; saved = 'Download only. Run Install-AgentSeat.cmd in the folder to install.' } }
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    $headers = @{ 'User-Agent' = 'agent-seat-installer'; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10' }
    Write-Host ($text.download -f $Version)
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/aksmfosef11/agent-seat/releases/tags/v$Version" -Headers $headers -TimeoutSec 30
    $asset = Get-AgentSeatReleaseAsset $release $Version
    $cache = Join-Path $env:LOCALAPPDATA ('agent-seat\Downloads\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $cache -AclObject $acl
    $zip = Join-Path $cache $asset.Name
    $checksums = Join-Path $cache 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri $asset.Checksums.browser_download_url -UseBasicParsing -OutFile $checksums -TimeoutSec 30
    Invoke-WebRequest -Uri $asset.Zip.browser_download_url -UseBasicParsing -OutFile $zip -TimeoutSec 600
    Assert-AgentSeatDownload $zip (Get-Content -LiteralPath $checksums -Raw) $asset
    $package = Join-Path $cache "agent-seat-$Version"
    Expand-AgentSeatArchive $zip $package
    Assert-AgentSeatPackage $package $Version
    Write-Host ($text.verified -f $package)
    if ($DownloadOnly) { Write-Host $text.saved; return $package }
    & (Join-Path $package 'Setup-Seat.ps1') -SeatId $SeatId -UserName $UserName -DisplayName $DisplayName -Language $Language -Plan:$Plan -NoOpen:$NoOpen -IAcceptUnsupportedWindowsClientPatch:$IAcceptUnsupportedWindowsClientPatch
}

# Dot-sourcing exposes the validation functions for offline tests, without network or host changes.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-AgentSeatBootstrap -Version $Version -SeatId $SeatId -UserName $UserName -DisplayName $DisplayName -Language $Language -DownloadOnly:$DownloadOnly -Plan:$Plan -NoOpen:$NoOpen -IAcceptUnsupportedWindowsClientPatch:$IAcceptUnsupportedWindowsClientPatch
}
