# Installation and recovery

[English](INSTALL.md) · [한국어](../INSTALL.md) · [简体中文](../zh-CN/INSTALL.md)

The preview installer targets Windows 11 Pro/Enterprise/Education x64. It does not provide automatic installation for Home, ARM64 or Windows Server/RDS. Use administrator access under the currently logged-in owner's account. The release ZIP includes the .NET runtime. Installation messages and original diagnostics are in English.

## First installation

1. Download the executable ZIP and SHA256SUMS.txt from [Releases](https://github.com/aksmfosef11/agent-seat/releases). Compare `Get-FileHash .\agent-seat-0.8.0-win-x64.zip -Algorithm SHA256` with the checksum, then extract it to `C:\agent-seat` or another folder.
2. Open 64-bit Administrator Windows PowerShell, enter that folder and run `.\Install.ps1` to review the plan.
3. Review the unsupported TermWrap modification for simultaneous Windows client sessions, then run `.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch`.
4. The installer creates a separate service, standard account `agent-seat-user`, hidden local RDP anchor task, approval list and token. The generated password is passed through stdin and stored with DPAPI, never displayed or placed in command arguments.
5. Double-click `View-Seat.cmd`, or run `& "$env:ProgramFiles\agent-seat\cli\agent-seat.exe" computer view --seat agent` as the ordinary installing owner.

If downloaded scripts are blocked, verify the download before using `Unblock-File .\Install.ps1` and `Get-ChildItem .\scripts\*.ps1 | Unblock-File`. Use a process-only execution policy if necessary; preserve machine and organization policies.

Existing SeatStream, friend accounts, Sunshine and gaming devices are retained. An active TermWrap dependency is reused without replacing it or restarting Terminal Services. The new default account is distinct from `seat-agent`; do not share one Windows seat account between the apps.

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

For removal, remove only the `agent-seat` service and `agent-seat RDP Anchor - …` tasks, then inspect the separate application/data folders and created accounts before cleanup. User profiles and work files are not automatically deleted. Distinguish every target from SeatStream.

## Shared RDP recovery

When applying TermWrap for the first time, `Install-MultiSession.ps1` backs up the registry configuration under `%ProgramData%\agent-seat\backups`. If the listener does not recover, use that installation's backup with `scripts\Restore-MultiSession.ps1 -BackupFile <backup-file> -Apply`.

Shared RDP rollback is separate from removing agent-seat. Save and close affected sessions and inspect all dependent apps before restoring it. Windows updates can change compatibility. This preview is not validated on every PC or Windows build.
