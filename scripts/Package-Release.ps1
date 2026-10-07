[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\releases'),
    [ValidatePattern('^8\.0\.[0-9]+$')][string]$RuntimeVersion = '8.0.31',
    [switch]$SkipBuild,
    [string]$PublishedDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$sourceCommit = $null
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    $sourceCommit = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
    if (& git -C $root status --porcelain) { throw 'Commit source changes before packaging a release.' }
}
$work = Join-Path $root ('artifacts\package-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$stage = Join-Path $work "agent-seat-$version-win-x64"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if ($SkipBuild) { $publish = if ($PublishedDirectory) { [IO.Path]::GetFullPath($PublishedDirectory) } else { Join-Path $root 'artifacts\publish' } }
else { & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $publish -SelfContained -RuntimeFrameworkVersion $RuntimeVersion }
foreach ($relative in @('service\AgentSeat', 'cli\agent-seat', 'service\rdp-anchor\AgentSeat.RdpAnchor', 'service\agent-helper\AgentSeat.AgentHelper')) {
    $configPath = Join-Path $publish "$relative.runtimeconfig.json"
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if (-not ($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -eq $RuntimeVersion })) { throw "Self-contained runtime version mismatch: $relative" }
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $configPath) 'hostfxr.dll'))) { throw "Self-contained host is missing: $relative" }
}
$termWrap = Join-Path $root 'artifacts\termwrap'
foreach ($file in @('TermWrap.dll', 'TermWrap.LICENSE', 'Zydis.LICENSE', 'Zycore.LICENSE', 'provenance.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $termWrap $file))) { throw "Missing TermWrap dependency $file. Run scripts\Build-TermWrap.ps1 first." }
}
New-Item -ItemType Directory -Path (Join-Path $stage 'artifacts') -Force | Out-Null
Copy-Item -LiteralPath $publish -Destination (Join-Path $stage 'artifacts\publish') -Recurse
Copy-Item -LiteralPath $termWrap -Destination (Join-Path $stage 'artifacts\termwrap') -Recurse
New-Item -ItemType Directory -Path (Join-Path $stage 'scripts'), (Join-Path $stage 'docs'), (Join-Path $stage 'licenses') -Force | Out-Null
foreach ($file in @('Install.ps1', 'Get-AgentSeat.ps1', 'Setup-Seat.ps1', 'Install-AgentSeat.cmd', 'View-Seat.cmd', 'README.md', 'README.ko.md', 'README.zh-CN.md', 'LICENSE', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage }
New-Item -ItemType Directory -Path (Join-Path $stage 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'assets\agent-seat-banner.svg') -Destination (Join-Path $stage 'assets')
foreach ($file in @('Install-MultiSession.ps1', 'Restore-MultiSession.ps1', 'Enable-AgentControl.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'scripts') }
foreach ($file in @('INSTALL.md', 'AGENT-USAGE.md', 'AGENT-MCP.md', 'VALIDATION.md', 'LOCALIZATION.md', 'AGENT-CONTROL.md', 'RELEASING.md')) { Copy-Item -LiteralPath (Join-Path $root "docs\$file") -Destination (Join-Path $stage 'docs') }
foreach ($locale in @('en', 'zh-CN')) { Copy-Item -LiteralPath (Join-Path $root "docs\$locale") -Destination (Join-Path $stage "docs\$locale") -Recurse }
$stagePrefix = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
foreach ($symbol in Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb') {
    if (-not $symbol.FullName.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbol outside staging folder.' }
    Remove-Item -LiteralPath $symbol.FullName
}
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
foreach ($runtime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.aspnetcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    foreach ($notice in @('LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT')) {
        $path = Join-Path $nugetRoot "$runtime\$RuntimeVersion\$notice"
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $stage "licenses\$runtime-$notice") }
    }
}
# Some NuGet runtime packs declare MIT but omit a license/notice file. Include unmodified upstream texts too.
$aspMetadata = [xml](Get-Content -LiteralPath (Join-Path $nugetRoot "microsoft.aspnetcore.app.runtime.win-x64\$RuntimeVersion\microsoft.aspnetcore.app.runtime.win-x64.nuspec") -Raw)
$aspCommit = [regex]::Match($aspMetadata.package.metadata.description, 'aspnetcore/tree/([0-9a-f]{40})').Groups[1].Value
if (-not $aspCommit) { throw 'ASP.NET runtime source commit is missing.' }
$upstreamNotices = [ordered]@{
    'aspnetcore-LICENSE.txt' = "https://raw.githubusercontent.com/dotnet/aspnetcore/$aspCommit/LICENSE.txt"
    'wpf-THIRD-PARTY-NOTICES.TXT' = "https://raw.githubusercontent.com/dotnet/wpf/v$RuntimeVersion/THIRD-PARTY-NOTICES.TXT"
    'winforms-THIRD-PARTY-NOTICES.TXT' = "https://raw.githubusercontent.com/dotnet/winforms/v$RuntimeVersion/THIRD-PARTY-NOTICES.TXT"
}
foreach ($notice in $upstreamNotices.GetEnumerator()) { Invoke-WebRequest $notice.Value -UseBasicParsing -OutFile (Join-Path $stage "licenses\$($notice.Key)") }
foreach ($required in @('microsoft.netcore.app.runtime.win-x64-LICENSE.TXT', 'microsoft.netcore.app.runtime.win-x64-THIRD-PARTY-NOTICES.TXT', 'microsoft.aspnetcore.app.runtime.win-x64-THIRD-PARTY-NOTICES.TXT', 'microsoft.windowsdesktop.app.runtime.win-x64-LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage "licenses\$required"))) { throw "Runtime notice missing: $required" }
}
$upstreamNotices | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'licenses\sources.json') -Encoding UTF8
# Refuse a contaminated staging folder rather than silently shipping local secrets or gaming binaries.
$bad = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -match '^(agent-token\.txt|agent-seats\.json|credential\.dat|appsettings.*\.json)$' -or $_.FullName -match '\\(sunshine|compat|app-launcher)\\' -or $_.Name -match '(SeatStream|ViGEm|SteamLauncher)' }
if ($bad) { throw "Unexpected private or gaming files in staging: $($bad.Name -join ', ')" }
@{ product = 'agent-seat'; version = $version; sourceCommit = $sourceCommit; runtime = 'win-x64'; selfContained = $true; dotnetRuntime = $RuntimeVersion; builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'release.json') -Encoding UTF8
$manifest = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = $_.FullName.Substring($stage.Length + 1).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$zip = Join-Path $output "agent-seat-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { throw "Release ZIP already exists: $zip. Choose a new output directory." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Copy-Item -LiteralPath (Join-Path $root 'Install-AgentSeat.cmd') -Destination $output
$launcherHash = (Get-FileHash -LiteralPath (Join-Path $output 'Install-AgentSeat.cmd') -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`r`n$launcherHash  Install-AgentSeat.cmd`r`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release ZIP: $zip"
Write-Host "SHA256: $hash"
Write-Host "Staging folder (for inspection): $stage"
