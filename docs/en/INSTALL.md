# Installation and recovery

[English](INSTALL.md) · [한국어](../INSTALL.md) · [简体中文](../zh-CN/INSTALL.md)

The preview installer targets Windows 11 Pro/Enterprise/Education x64. It does not provide automatic installation for Home, ARM64 or Windows Server/RDS. Use administrator access under the currently logged-in owner's account. The release ZIP includes the .NET runtime. Installation messages and original diagnostics are in English.

## Guided installation

Download [Install-AgentSeat.cmd](https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.1/Install-AgentSeat.cmd) and double-click it, or use one command in 64-bit Windows PowerShell:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.1/Get-AgentSeat.ps1')))
```

The download is pinned to 0.9.1. The bootstrap validates the ZIP's size, SHA-256 checksum and GitHub digest; checks every archive path; verifies the extracted manifest; then starts setup. Review the plan, type INSTALL to accept the unsupported TermWrap change and approve UAC using the same interactive owner account. After installation the normal owner process opens the viewer read-only. Administrator access under a different Windows account is refused before installation.

The guided setup messages follow Windows' UI language (English, Korean or Simplified Chinese). Use `-Language en`, `ko` or `zh` to select one. Detailed backend diagnostics stay in English. Windows security prompts are handled by the user. These preview scripts are not code-signed; review the source and download origin before running them. The launcher uses a process-only execution policy and does not change machine or organization policy.

Download without installing or requesting administrator access:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.1/Get-AgentSeat.ps1'))) -DownloadOnly
```

The verified ZIP and extracted files are retained under `%LOCALAPPDATA%\agent-seat\Downloads\<unique folder>`. For a downloaded ZIP, run `Setup-Seat.ps1 -Plan` to review without host changes, or `Setup-Seat.ps1 -NoOpen` to install without opening the viewer. When a seat is already configured, setup keeps it and can open its viewer. It does not upgrade existing service files. A stopped or partially installed service is left for diagnosis rather than replaced.

For a completely offline installation, download the ZIP and SHA256SUMS.txt, compare `Get-FileHash .\agent-seat-0.9.1-win-x64.zip -Algorithm SHA256`, extract and double-click its Install-AgentSeat.cmd. No bootstrap download is needed. If verified downloaded scripts are blocked, use `Unblock-File` on those files only; preserve organization policy.

The low-level installer remains available in Administrator PowerShell for automation:

```powershell
.\Install.ps1
.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch
```

It creates the dedicated service, standard account, local RDP anchor, approval list and protected token. The generated password is passed through stdin and stored with DPAPI, never displayed or placed in command arguments. An active TermWrap dependency is reused without replacing it or restarting Terminal Services. Each seat requires its own Windows account.

## Viewer and language

The viewer starts read-only and refreshes roughly every 600 ms while visible. Stop the AI before enabling **Manual control**. Clicks, double clicks, right clicks, drags, wheel input and ordinary keys are supported. Use the text box for IME input and shortcut buttons for combinations intercepted by the browser. Capture and input are blocked while the owner has paused the seat.

Choose English, Korean or Simplified Chinese in the dashboard or viewer. The browser language is used initially; an explicit choice is saved locally. Switching does not reload the page or erase a text draft. It does not change Windows or application languages. The viewer cookie expires after 30 minutes; run `computer view` again. Human viewing makes no model API calls.

## Additional seats

Use a new ID and account name. An existing agent-seat service is reused; this command adds a seat without updating its binaries:

```powershell
.\Install.ps1 -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2' -Apply -IAcceptUnsupportedWindowsClientPatch
```

Existing accounts and IDs are refused. Passwords are not reset. There is no in-place updater in this preview; review service, anchor and configuration backups before replacing installed files.

## Interrupted installation

The installer does not delete accounts or roll back shared RDP automatically. Inspect `Get-Service agent-seat`, `Get-ScheduledTask -TaskName 'agent-seat RDP Anchor - *'` and CLI `computer status`. Reusing a partially created account name intentionally fails. Repair that seat's anchor after inspecting the state, or add a seat with a new name.

Some Windows builds show privacy or first-login setup inside the new session. Complete that setup in the seat viewer. If no session connects, check the anchor's `status --seat agent` under the installing owner. Its configuration and encrypted credential are under `%ProgramData%\agent-seat\RdpAnchors\<owner SID>`. Logging out closes the owner's anchor; log in and use `computer start` again. Restart/re-login behavior is still part of preview acceptance testing.

## Stop or remove only agent-seat

Stop the AI and save open files. `computer close-seat --seat agent --keep-files` logs off only the AI session. Administrator command `Stop-Service agent-seat` stops the new service. Do not stop TermService or roll back shared TermWrap to stop this app.

For removal, remove only the `agent-seat` service and `agent-seat RDP Anchor - …` tasks, then inspect the separate application/data folders and created accounts before cleanup. User profiles and work files are not automatically deleted. Remove only accounts and files created for agent-seat.

## Shared RDP recovery

When applying TermWrap for the first time, `Install-MultiSession.ps1` backs up the registry configuration under `%ProgramData%\agent-seat\backups`. If the listener does not recover, use that installation's backup with `scripts\Restore-MultiSession.ps1 -BackupFile <backup-file> -Apply`.

Shared RDP rollback is separate from removing agent-seat. Save and close affected sessions and inspect all dependent apps before restoring it. Windows updates can change compatibility. This preview is not validated on every PC or Windows build.
