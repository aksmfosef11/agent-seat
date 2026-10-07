# 0.9.0 preview validation

Validation date: 2026-10-07. This is an experimental preview, not a stable release.

0.9.0 adds a standalone double-click launcher and version-pinned one-command bootstrap. It downloads and verifies the executable release, extracts it safely and invokes the guided installer. Setup supports English, Korean and Simplified Chinese, explicit acceptance of the unsupported Windows change, same-owner UAC elevation and reopening an existing seat without reinstalling it. Public introductions describe the agent desktop and installation workflow.

The seat protocol and trilingual web interface from 0.8.0 remain unchanged. The development PC's installed service is still 0.7.0 pending administrator approval; its UI was tested using a read-only development preview. These download/setup checks do not claim a fresh-machine installation.

## Completed

- Managed tests: 227 Core and 36 Windows tests passed (`dotnet test AgentSeat.sln -c Release`).
- Installer: 10 offline test groups passed in Windows PowerShell 5.1 (`powershell -NoProfile -File tests/installer/bootstrap.tests.ps1`). They cover exact release origins, SHA-256/asset digests, corrupt/ambiguous checksums, safe extraction, traversal/ADS/duplicate path refusal, manifests and modified files, literal elevation arguments, cancelled consent, existing-seat reuse and same-owner checks. No services/accounts or UAC prompts are changed by these tests.
- Web interface: 12 tests passed (`npm test`). Six cover coordinate scaling, key mapping, wheel/drag actions and serialized input failure/cancellation; six cover locale selection, complete translation dictionaries, placeholder parity, static message keys, safe interpolation, host checks, errors and seat states. Total managed/web/installer tests: 285.
- Chrome displayed the dashboard and live read-only viewer in all three languages. Switching languages preserved the open viewer, text draft and disabled manual-control state; reloading retained the selected language. The Chinese viewer fit a 420-pixel viewport without horizontal overflow. The browser reported no JavaScript warnings/errors during these checks. No input was sent to the seat for localization checks.
- A separately installed `agent-seat` service, account and live desktop worked alongside an existing SeatStream installation. The original SeatStream and Terminal Services process IDs and the original settings hashes stayed unchanged during installation.
- CLI observation returned a 1280×800 image from the new user's session. The browser viewer displayed that session; live clicks and right clicks were observed.
- Read-only HTTP integration checks passed for bearer-only ticket issuance, one-use redemption, seat-scoped HttpOnly/SameSite cookies, replay refusal, and refusal to mint tickets with a viewer cookie.
- The installed MCP server reported version 0.7.0, exposed its four tools and returned an image on its first observation. Run the optional checks with `node tests/integration/windows-live.js agent` as the installing owner. Set `AGENTSEAT_CLI` to a published CLI to test a new build against the installed service, and `AGENTSEAT_EXPECTED_VERSION` to verify its exact MCP version.
- A limited-owner scheduled task launched the repaired RDP anchor from the published binaries, read its protected ProgramData configuration and connected the new desktop at 1280×800.
- Self-contained Windows x64 applications were built with .NET 8.0.31. Release packaging includes TermWrap provenance, original dependency licenses, a file manifest and a ZIP checksum, and rejects private configuration and gaming binaries.

## Pending and known limits

The first installed scheduled RDP anchor could not read its configuration under LocalAppData. The installer and anchor now explicitly use a protected ProgramData directory scoped to the owner's Windows SID. A limited-owner scheduled task passed credential decryption and connected the desktop with that fix. Updating the earlier installed service files and production task on the development PC still requires the owner's Windows administrator approval; the working desktop currently uses the diagnostic task's repaired anchor.

A fresh install on a disposable Windows machine, the guided installer's actual UAC/apply flow, restart/re-login, installation on other Windows builds, and adding a second agent-seat account have not been validated. Full browser gesture coverage, IME text submission and interaction while another AI is running also need live acceptance testing. Stop the AI before manual control; there is no exclusive human/AI input lock.

TermWrap changes the shared Windows RDP service. Its compatibility depends on the installed Windows build. A separate app name, account and service do not create a virtual machine or security sandbox. See [installation and recovery](INSTALL.md) and [security](../SECURITY.md).
