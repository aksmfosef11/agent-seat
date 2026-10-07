# Interactive entry point for a verified, extracted release. Low-level Install.ps1 remains available.
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z][a-z0-9-]{0,31}$')][string]$SeatId = 'agent',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$')][string]$UserName = 'agent-seat-user',
    [string]$DisplayName = 'AI Desktop',
    [ValidateSet('auto', 'en', 'ko', 'zh')][string]$Language = 'auto',
    [switch]$Plan,
    [switch]$NoOpen,
    [switch]$IAcceptUnsupportedWindowsClientPatch,
    [switch]$Elevated,
    [string]$ExpectedOwnerSid,
    [int]$ExpectedSessionId = -1
)

function New-AgentSeatElevationCommand {
    param([string]$ScriptPath, [hashtable]$Parameters)
    # Data is encoded separately, so names containing quotes, $, ` or newlines cannot become PowerShell code.
    $data = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Parameters | ConvertTo-Json -Compress)))
    $literalPath = $ScriptPath.Replace("'", "''")
    $code = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$request = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__DATA__')))
$arguments = @{}
foreach ($property in $request.PSObject.Properties) { $arguments[$property.Name] = $property.Value }
try { & '__SCRIPT__' @arguments; exit 0 }
catch { Write-Host $_.Exception.Message -ForegroundColor Red; Read-Host 'Press Enter / Enter를 누르세요 / 按 Enter'; exit 1 }
'@
    $code = $code.Replace('__DATA__', $data).Replace('__SCRIPT__', $literalPath)
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
}

function Invoke-AgentSeatSetup {
    param([string]$Root, [string]$ScriptPath, [hashtable]$Options)
    $ErrorActionPreference = 'Stop'
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($Options.Elevated -and ($owner.User.Value -ne $Options.ExpectedOwnerSid -or $sessionId -ne $Options.ExpectedSessionId)) { throw 'Approve UAC using the same interactive Windows owner account. Installation was not started.' }
    if ($owner.IsSystem -or $sessionId -eq 0) { throw 'Run setup as the interactive Windows owner.' }
    . (Join-Path $Root 'Get-AgentSeat.ps1')
    $metadata = Get-Content -LiteralPath (Join-Path $Root 'release.json') -Raw | ConvertFrom-Json
    Assert-AgentSeatPackage $Root $metadata.version
    $locale = if ($Options.Language -eq 'auto') { [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName } else { $Options.Language }
    $text = switch ($locale) {
        'ko' { @{
            title = 'agent-seat 설치'; intro = 'AI용 Windows 계정과 별도 데스크톱 세션을 만듭니다.'
            patch = '동시 세션을 위해 비공식 TermWrap 수정을 사용합니다. 첫 적용 시 RDP 세션이 끊길 수 있으며 Windows 업데이트로 호환성이 바뀔 수 있습니다.'
            consent = '설치에 동의하면 INSTALL을 입력하세요 (그 외 입력은 취소)'; cancelled = '설치를 취소했습니다. 다운로드 파일은 보관됩니다.'
            elevate = '설치에 관리자 권한이 필요합니다. Windows 승인창을 확인하세요.'
            installed = '이미 설치되어 있습니다 (v{0}). 기존 실행 파일은 업데이트하지 않습니다.'
            success = '좌석 설치 완료. 화면 보기는 읽기 전용으로 열립니다.'
            failed = '설치가 완료되지 않았습니다. 표시된 오류와 docs/INSTALL.md의 복구 안내를 확인하세요.'
        } }
        'zh' { @{
            title = '安装 agent-seat'; intro = '为 AI 创建专用 Windows 账户和独立桌面会话。'
            patch = '并发会话使用非官方 TermWrap 修改。首次应用可能中断 RDP 会话；Windows 更新可能影响兼容性。'
            consent = '同意安装请输入 INSTALL（其他输入取消）'; cancelled = '已取消安装。下载文件已保留。'
            elevate = '安装需要管理员权限，请确认 Windows 授权提示。'
            installed = '已安装 (v{0})。现有程序文件不会更新。'
            success = '席位安装完成。查看器将以只读模式打开。'
            failed = '安装未完成。请查看错误和 docs/zh-CN/INSTALL.md 的恢复说明。'
        } }
        default { @{
            title = 'agent-seat setup'; intro = 'Create a dedicated Windows account and desktop session for your AI.'
            patch = 'Concurrent sessions use an unsupported TermWrap modification. First application may disconnect RDP sessions; Windows updates can affect compatibility.'
            consent = 'Type INSTALL to accept and install (anything else cancels)'; cancelled = 'Installation cancelled. Downloaded files were retained.'
            elevate = 'Administrator access is required. Review the Windows UAC prompt.'
            installed = 'Already installed (v{0}). Existing application binaries are not updated.'
            success = 'Seat setup complete. The viewer opens read-only.'
            failed = 'Setup did not complete. Review the error and recovery guide in docs/en/INSTALL.md.'
        } }
    }
    Write-Host "`n$($text.title) $($metadata.version)" -ForegroundColor Cyan
    Write-Host $text.intro
    $installOptions = @{ SeatId = $Options.SeatId; UserName = $Options.UserName; DisplayName = $Options.DisplayName }
    if ($Options.Plan) { & (Join-Path $Root 'Install.ps1') @installOptions; return }
    $service = Get-Service -Name agent-seat -ErrorAction SilentlyContinue
    $existing = $false
    if ($service) {
        $health = Invoke-RestMethod 'http://127.0.0.1:38399/api/v1/health' -TimeoutSec 5
        if ($health.mode -ne 'agent-seat') { throw 'Unexpected service on port 38399.' }
        $seats = Invoke-RestMethod 'http://127.0.0.1:38399/api/v1/seats' -TimeoutSec 5
        $match = @($seats | Where-Object { $_.seat.id -eq $Options.SeatId })
        if ($match.Count) {
            if ($match[0].seat.userName -ine $Options.UserName) { throw 'Seat ID belongs to a different Windows account.' }
            Write-Host ($text.installed -f $health.version)
            $existing = $true
        }
    }
    if (-not $existing) {
        & (Join-Path $Root 'Install.ps1') @installOptions
        Write-Host $text.patch -ForegroundColor Yellow
        if (-not $Options.IAcceptUnsupportedWindowsClientPatch) {
            if ((Read-Host $text.consent) -cne 'INSTALL') { Write-Host $text.cancelled; return }
        }
        $principal = [Security.Principal.WindowsPrincipal]::new($owner)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            if ($Options.Elevated) { throw 'Administrator access was not granted.' }
            Write-Host $text.elevate
            $childOptions = @{
                SeatId = $Options.SeatId; UserName = $Options.UserName; DisplayName = $Options.DisplayName; Language = $Options.Language
                NoOpen = $true; Elevated = $true; IAcceptUnsupportedWindowsClientPatch = $true
                ExpectedOwnerSid = $owner.User.Value; ExpectedSessionId = $sessionId
            }
            $encoded = New-AgentSeatElevationCommand $ScriptPath $childOptions
            $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            try { $child = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Normal -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Wait -PassThru }
            catch { throw "$($text.cancelled) $($_.Exception.Message)" }
            if ($child.ExitCode -ne 0) { throw $text.failed }
        } else { & (Join-Path $Root 'Install.ps1') @installOptions -Apply -IAcceptUnsupportedWindowsClientPatch }
        Write-Host $text.success -ForegroundColor Green
    }
    if (-not $Options.NoOpen) {
        $cli = Join-Path $env:ProgramFiles 'agent-seat\cli\agent-seat.exe'
        & $cli computer view --seat $Options.SeatId
        if ($LASTEXITCODE -ne 0) { throw 'Could not open the viewer. Run agent-seat computer view again.' }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-AgentSeatSetup -Root $PSScriptRoot -ScriptPath $PSCommandPath -Options @{
        SeatId = $SeatId; UserName = $UserName; DisplayName = $DisplayName; Language = $Language; Plan = [bool]$Plan
        NoOpen = [bool]$NoOpen; IAcceptUnsupportedWindowsClientPatch = [bool]$IAcceptUnsupportedWindowsClientPatch
        Elevated = [bool]$Elevated; ExpectedOwnerSid = $ExpectedOwnerSid; ExpectedSessionId = $ExpectedSessionId
    }
}
