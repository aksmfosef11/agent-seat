[CmdletBinding()]
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [switch]$SelfContained,
    [ValidatePattern('^$|^8\.0\.[0-9]+$')][string]$RuntimeFrameworkVersion = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$runtimeArguments = @()
if ($RuntimeFrameworkVersion) { $runtimeArguments = @("-p:AgentSeatRuntimeVersion=$RuntimeFrameworkVersion") }
$targets = [ordered]@{
    'AgentSeat.Service' = 'service'
    'AgentSeat.Cli' = 'cli'
    'AgentSeat.RdpAnchor' = 'service\rdp-anchor'
    'AgentSeat.AgentHelper' = 'service\agent-helper'
}
foreach ($target in $targets.GetEnumerator()) {
    $project = Join-Path $projectRoot "src\$($target.Key)\$($target.Key).csproj"
    $destination = Join-Path $OutputDirectory $target.Value
    dotnet publish $project -c Release -r $Runtime --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o $destination @runtimeArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($target.Key)" }
    Write-Host "$($target.Key): $destination"
}
Write-Host 'agent-seat published. Sunshine, Steam launcher, AppCompat and gamepad drivers are excluded.'
