# 0.9.3 preview validation

Validation date: 2026-10-12. This is an experimental preview, not a stable release.

0.9.3 fixes the guided installer reporting failure after a successful setup. Windows PowerShell 5.1 sends a JSON array from `Invoke-RestMethod` down the pipeline as one object, so the installer never matched the live seat session and always ended with "No live seat session after 90 seconds" while leaving the installation record incomplete. The same pattern made repair fail with "Duplicate seat identity" when more than one seat was registered. The readiness check also treated a finished seat as needing repair on every run, and `Install.ps1` accepted resolutions below the 800×600 the seat service requires, failing after the Windows account had been created. The `agent-seat add --agent` message now names the installer that exists, and an [AI installation guide](AGENT-INSTALL.md) is added in three languages.

The service, seat protocol and trilingual web interface are unchanged since 0.9.2. The installer scripts changed as described; the release has no service/CLI updater, so an existing installation keeps its binaries.

## Completed

- Managed tests: 227 Core and 36 Windows tests passed (`dotnet test AgentSeat.sln -c Release`).
- Web interface: 12 tests passed (`npm test`).
- Bootstrap: 12 offline test groups passed in Windows PowerShell 5.1 (`powershell -NoProfile -File tests/installer/bootstrap.tests.ps1`).
- Recovery: 11 test groups passed in Windows PowerShell 5.1 (`powershell -NoProfile -File tests/installer/recovery.tests.ps1`). Real artifact directories, protected DACLs, atomic journal replacement and owner-bound DPAPI encryption are used; Windows services, local accounts, scheduled tasks, registry reads and API calls are simulated. The simulated `Invoke-RestMethod` now returns arrays as one pipeline object and a trigger-less task has `$null` triggers, as on Windows PowerShell 5.1. Three groups are new: the real session wait against an array response, repair of a seat while another seat is registered, and installer resolution limits cross-checked against the seat service's minimum. Each new group fails against the 0.9.2 scripts. Total managed/web/bootstrap/recovery checks: 298, including grouped installer scenarios.
- Live check on the development PC (Windows 11 Pro build 26100, another desktop service's TermWrap already active). The old 0.7.0 `agent-seat` service, anchor task and data were removed; the other service, its TermWrap and its accounts and sessions were left alone. Guided setup of the published 0.9.2 package with a real UAC prompt created the service, a new standard account, the seat and the anchor task, and the seat session became active, but the 0.9.2 installer reported the 90-second failure above and left the record incomplete. That reproduced the defect. Setup was then completed with the fixed `Install.ps1` and `AgentSeat-Installation.ps1` run from a copy of the 0.9.2 package with a refreshed manifest: the session wait matched within seconds, the record became complete, and the installer's own readiness check passed. The existing TermWrap was reused; the Terminal Services host process kept its boot-time start and its module path did not change.
- On that seat, `computer status` reported an available session and `computer begin` returned a 1280×800 image of the seat's own desktop. The AI installation guide's administrator check, package download, plan and seat verification commands were run (the plan with this PC's account name). Its install step was not repeated on that PC.
- Self-contained Windows x64 applications are built with .NET 8.0.31. Release packaging includes TermWrap provenance, original dependency licenses, a file manifest and a ZIP checksum, and rejects private configuration and gaming binaries.

## Pending and known limits

The 0.9.3 release package itself has not yet been used for a fresh installation that creates the account and waits for its first session in one run. The fixed session wait was exercised live while resuming an interrupted setup, and the fresh path is covered by simulation only.

A fresh install on a disposable Windows machine, restart/re-login, installation on other Windows builds, first-time TermWrap application, and adding a second agent-seat account on a real machine have not been validated. Recovery simulations cannot establish TermWrap compatibility or first-login behavior on a real machine. The 0.9.2 browser, HTTP-authorization and MCP checks were not repeated because the service, web interface and CLI protocol are unchanged. Full browser gesture coverage, IME text submission and interaction while another AI is running also need live acceptance testing. Stop the AI before manual control; there is no exclusive human/AI input lock.

The guided installer's UAC step must be approved by the owner; an AI can neither see nor click it. See the [AI installation guide](AGENT-INSTALL.md) for how an AI hands that step back to the user.

TermWrap changes the shared Windows RDP service. Its compatibility depends on the installed Windows build. A separate app name, account and service do not create a virtual machine or security sandbox. See [installation and recovery](INSTALL.md) and [security](../SECURITY.md).
