@echo off
set "agentSeatCli=%ProgramFiles%\agent-seat\cli\agent-seat.exe"
if not exist "%agentSeatCli%" (
  echo agent-seat is not installed. See README.md and Install.ps1.
  pause
  exit /b 1
)
if "%~1"=="" (
  "%agentSeatCli%" computer view --seat agent
) else (
  "%agentSeatCli%" computer view --seat "%~1"
)
