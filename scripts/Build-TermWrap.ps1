[CmdletBinding()]
param(
    [string]$SourceRoot,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\termwrap')
)

$ErrorActionPreference = 'Stop'
$termWrapCommit = '0da3ac901ab04509d608285caa403a8d1f14f12c'
$repositoryUrl = 'https://github.com/DuoStream/TermWrap.git'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Executable $($Arguments -join ' ')"
    }
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $projectRoot ".build\TermWrap-$termWrapCommit"
    if (-not (Test-Path -LiteralPath $SourceRoot)) {
        $buildParent = Split-Path -Parent $SourceRoot
        New-Item -ItemType Directory -Path $buildParent -Force | Out-Null
        Invoke-Native git @('clone', '--recurse-submodules', $repositoryUrl, $SourceRoot)
        Invoke-Native git @('-C', $SourceRoot, 'checkout', '--detach', $termWrapCommit)
    }
}

$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot '.git'))) {
    throw "SourceRoot is not a Git checkout: $SourceRoot"
}

Invoke-Native git @('-C', $SourceRoot, 'submodule', 'sync', '--recursive')
Invoke-Native git @('-C', $SourceRoot, 'submodule', 'update', '--init', '--recursive')
$actualCommit = (& git -C $SourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $termWrapCommit) {
    throw "Refusing to build unpinned TermWrap source. Expected $termWrapCommit, found $actualCommit."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    throw 'MSBuild was not found. Install Visual Studio with Desktop development with C++.'
}

$previousCl = [Environment]::GetEnvironmentVariable('CL', 'Process')
$previousUnderscoreCl = [Environment]::GetEnvironmentVariable('_CL_', 'Process')
try {
    [Environment]::SetEnvironmentVariable('CL', '/DZYDIS_STATIC_BUILD /DZYCORE_STATIC_BUILD', 'Process')
    [Environment]::SetEnvironmentVariable('_CL_', '/GS-', 'Process')
    Invoke-Native $msbuild @(
        (Join-Path $SourceRoot 'zydis\msvc\Zydis.sln'),
        '/m', '/t:Zydis', '/p:Configuration=Release MT', '/p:Platform=x64', '/v:minimal'
    )
    # TermWrap's headers must also see the static-library defines; otherwise they request __imp_Zydis*.
    Invoke-Native $msbuild @(
        (Join-Path $SourceRoot 'TermWrap.sln'),
        '/m', '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=x64', '/v:minimal'
    )
}
finally {
    [Environment]::SetEnvironmentVariable('CL', $previousCl, 'Process')
    [Environment]::SetEnvironmentVariable('_CL_', $previousUnderscoreCl, 'Process')
}

$builtDll = Join-Path $SourceRoot 'x64\Release\TermWrap.dll'
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Build succeeded but output was not found: $builtDll"
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputDll = Join-Path $OutputDirectory 'TermWrap.dll'
Copy-Item -LiteralPath $builtDll -Destination $outputDll -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'TermWrap.LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'zydis\LICENSE') -Destination (Join-Path $OutputDirectory 'Zydis.LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'zydis\dependencies\zycore\LICENSE') -Destination (Join-Path $OutputDirectory 'Zycore.LICENSE') -Force

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputDll).Hash
$provenance = [ordered]@{
    repository = $repositoryUrl
    commit = $termWrapCommit
    sha256 = $hash
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8

Write-Host "Built:  $outputDll"
Write-Host "SHA256: $hash"
