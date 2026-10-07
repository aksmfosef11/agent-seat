# Publishing source and releases

Use the `aksmfosef11/agent-seat` repository. GitHub Desktop can commit and push using its existing signed-in account; no token needs to be copied to a CLI.

Before the first push, check that Changes includes only source, tests, documentation and scripts. `artifacts/`, `.build/`, bin/obj, logs, keys, tokens and credentials are ignored. Never force-add those folders. The Windows seat password, local bearer token and installed configuration are machine-specific and must not be shipped.

Run the managed and UI tests, build the pinned TermWrap dependency, then run `scripts\Package-Release.ps1`. The output ZIP and SHA256SUMS.txt are under `artifacts\releases` and intentionally stay out of the source commit. A ZIP includes its own file manifest and dependencies' original license files. The file manifest checks integrity after extraction; the separately published ZIP checksum is the download verification value.

In GitHub Desktop, select **agent-seat**, review Changes, commit the source, then Push origin. On GitHub, create a release for tag `v0.7.1`, mark it **pre-release**, and upload the ZIP plus SHA256SUMS.txt. Use the exact source commit used to build those assets. The automatically generated Source code archives are developer downloads and are not the end-user installer.

The preview installer has been prepared for Windows 11 x64 Pro/Enterprise/Education. Before calling a release stable, test extraction, checksum verification, fresh-machine install, ordinary-user CLI/viewer control, restart/re-login, additional seats, coexistence with SeatStream and recovery in a disposable Windows environment. Record what was tested and leave untested combinations explicit.

Do not bundle Sunshine, Steam launchers, AppCompat injectors, gamepad drivers, private Duo binaries or Windows system DLLs. Update the .NET runtime pin and third-party provenance deliberately when preparing later versions.
