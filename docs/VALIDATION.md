# 0.7.0 preview validation

Validation date: 2026-10-07. This is an experimental preview, not a stable release.

## Completed

- Managed tests: 227 Core and 36 Windows tests passed (`dotnet test AgentSeat.sln -c Release`).
- Browser input logic: 6 tests passed (`node --test tests/ui/remote-control.test.js`). These cover coordinate scaling, key mapping, wheel/drag actions and serialized input failure/cancellation behavior.
- A separately installed `agent-seat` service, account and live desktop worked alongside an existing SeatStream installation. The original SeatStream and Terminal Services process IDs and the original settings hashes stayed unchanged during installation.
- CLI observation returned a 1280×800 image from the new user's session. The browser viewer displayed that session; live clicks and right clicks were observed.
- Read-only HTTP integration checks passed for bearer-only ticket issuance, one-use redemption, seat-scoped HttpOnly/SameSite cookies, replay refusal, and refusal to mint tickets with a viewer cookie.
- The installed MCP server reported version 0.7.0, exposed its four tools and returned an image on its first observation. Run the optional checks with `node tests/integration/windows-live.js agent` as the installing owner.
- A limited-owner scheduled task launched the repaired RDP anchor from the published binaries, read its protected ProgramData configuration and connected the new desktop at 1280×800.
- Self-contained Windows x64 applications were built with .NET 8.0.31. Release packaging includes TermWrap provenance, original dependency licenses, a file manifest and a ZIP checksum, and rejects private configuration and gaming binaries.

## Pending and known limits

The first installed scheduled RDP anchor could not read its configuration under LocalAppData. The installer and anchor now explicitly use a protected ProgramData directory scoped to the owner's Windows SID. A limited-owner scheduled task passed credential decryption and connected the desktop with that fix. Updating the earlier installed service files and production task on the development PC still requires the owner's Windows administrator approval; the working desktop currently uses the diagnostic task's repaired anchor.

A fresh install on a disposable Windows machine, restart/re-login, installation on other Windows builds, and adding a second agent-seat account have not been validated. Full browser gesture coverage, IME text submission and interaction while another AI is running also need live acceptance testing. Stop the AI before manual control; there is no exclusive human/AI input lock.

TermWrap changes the shared Windows RDP service. Its compatibility depends on the installed Windows build. A separate app name, account and service do not create a virtual machine or security sandbox. See [installation and recovery](INSTALL.md) and [security](../SECURITY.md).
