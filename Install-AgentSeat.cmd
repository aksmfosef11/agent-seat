@echo off
setlocal DisableDelayedExpansion
title agent-seat setup
set "AGENTSEAT_WINDOWS_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "AGENTSEAT_WINDOWS_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%~dp0Setup-Seat.ps1" goto download
if not exist "%~dp0release.json" goto download
if not exist "%~dp0manifest.json" goto download
"%AGENTSEAT_WINDOWS_PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Seat.ps1"
goto complete
:download
"%AGENTSEAT_WINDOWS_PS%" -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12; & ([scriptblock]::Create((Invoke-RestMethod 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.0/Get-AgentSeat.ps1')))"
:complete
set "AGENTSEAT_SETUP_RESULT=%ERRORLEVEL%"
if not "%AGENTSEAT_SETUP_RESULT%"=="0" (
    echo.
    echo Setup did not complete. Review the message above.
    pause
)
exit /b %AGENTSEAT_SETUP_RESULT%
