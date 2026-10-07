# Publishing source and releases

Use the `aksmfosef11/agent-seat` repository. GitHub Desktop can commit and push using its existing signed-in account; no token needs to be copied to a CLI.

Before the first push, check that Changes includes only source, tests, documentation and scripts. `artifacts/`, `.build/`, bin/obj, logs, keys, tokens and credentials are ignored. Never force-add those folders. The Windows seat password, local bearer token and installed configuration are machine-specific and must not be shipped.

Run the managed and UI tests, plus both Windows PowerShell installer suites: `powershell -NoProfile -File tests/installer/bootstrap.tests.ps1` and `powershell -NoProfile -File tests/installer/recovery.tests.ps1`. Recovery tests use real filesystem/DPAPI with simulated host operations; use a disposable Windows machine for actual install/reboot acceptance. Build the pinned TermWrap dependency, then run `scripts\Package-Release.ps1`. The output ZIP and SHA256SUMS.txt are under `artifacts\releases` and intentionally stay out of the source commit. A ZIP includes its own file manifest and dependencies' original license files. The file manifest checks integrity after extraction; the separately published ZIP checksum is the download verification value.

In GitHub Desktop, select **agent-seat**, review Changes, commit the source, then Push origin. On GitHub, create a release for tag `v0.9.2`, mark it **pre-release**, and upload the ZIP, standalone Install-AgentSeat.cmd and SHA256SUMS.txt. Use the exact source commit used to build those assets. The automatically generated Source code archives are developer downloads and are not the end-user installer.

The preview installer has been prepared for Windows 11 x64 Pro/Enterprise/Education. Before calling a release stable, test extraction, checksum verification, fresh-machine install, ordinary-user CLI/viewer control, restart/re-login, additional seats, coexistence with other RDP applications and recovery in a disposable Windows environment. Record what was tested and leave untested combinations explicit.

Do not bundle Sunshine, Steam launchers, AppCompat injectors, gamepad drivers, private Duo binaries or Windows system DLLs. Update the .NET runtime pin and third-party provenance deliberately when preparing later versions.

The bootstrap default version and the standalone launcher raw URL must match the release tag. Publish the tag and all three assets before advertising its one-command install. Keep `Get-AgentSeat.ps1` ASCII without BOM for HTTP evaluation, and `Setup-Seat.ps1` UTF-8 with BOM for Windows PowerShell 5.1 translations. Exercise the exact public one-command expression in Windows PowerShell 5.1 with `-Plan -NoOpen`; running only the downloaded `.ps1` file will miss HTTP decoding failures. UAC, clean-machine installation and restart acceptance remain user-driven tests.
