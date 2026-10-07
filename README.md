# agent-seat

Give your AI a separate Windows desktop. Keep using your own screen while the AI observes and operates a dedicated local user's session. Open that session in a local browser to see its screen or take over with your mouse and keyboard.

AI용 독립 Windows 좌석입니다. CLI와 선택 사항인 MCP를 모두 지원하고, 브라우저에서 좌석 화면 확인과 직접 조작을 제공합니다. Sunshine, Moonlight, Steam, 게임패드 드라이버는 배포본에 포함하지 않습니다.

**0.7.0 experimental preview.** The installer targets Windows 11 Pro/Enterprise/Education **x64** and requires administrator access. It uses an unsupported TermWrap modification for simultaneous sessions on Windows clients. Home and ARM64 are not supported by this installer. A clean-machine install has not yet been validated across Windows builds; review [validation and remaining checks](docs/VALIDATION.md) and [installation and recovery](docs/INSTALL.md) before applying it. Windows updates may require recovery or a dependency update.

## Install a release

1. Download `agent-seat-0.7.0-win-x64.zip` and `SHA256SUMS.txt` from [GitHub Releases](https://github.com/aksmfosef11/agent-seat/releases). A source-code ZIP alone does not contain built executables. If a release has not been published yet, build it below.
2. Check the ZIP's SHA-256 with `Get-FileHash`, then extract it to a folder such as `C:\agent-seat`. The release includes the .NET runtime; end users do not need Visual Studio, Node.js, Git or a .NET SDK.
3. Open **64-bit Windows PowerShell as Administrator**, go to the extracted folder, and review the dry run:

```powershell
cd C:\agent-seat
.\Install.ps1
```

4. Install after reviewing the Windows client modification:

```powershell
.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch
```

If downloaded scripts are blocked, run `Unblock-File .\Install.ps1` and `Get-ChildItem .\scripts\*.ps1 | Unblock-File` after verifying the download. A temporary process-only execution policy can be used if needed; do not change the machine policy.

5. Double-click `View-Seat.cmd`, or open the screen from your normal Windows account:

```powershell
& "$env:ProgramFiles\agent-seat\cli\agent-seat.exe" computer view --seat agent
```

The viewer starts read-only. Stop the AI's current task before enabling **직접 조작 / manual control**. It supports clicks, double clicks, right clicks, drags, wheel scrolling and keyboard shortcuts. Use the text box for Korean or other IME text. The browser view refreshes roughly every 600 ms while visible; it is intended for checking and operating desktop applications.

## Coexists with SeatStream

| Component | agent-seat | Existing SeatStream |
| --- | --- | --- |
| Windows service | `agent-seat` | `SeatStream` |
| App and CLI | `%ProgramFiles%\agent-seat` | `%ProgramFiles%\SeatStream` |
| Data and bearer token | `%ProgramData%\agent-seat` | `%ProgramData%\SeatStream` |
| API/UI port | `38399`, loopback only | `38299` |
| Default Windows seat user | `agent-seat-user` | existing users retained |
| RDP anchor tasks | `agent-seat RDP Anchor - …` | existing tasks retained |
| Helper pipe / process | `AgentSeat.Agent.…` / `AgentSeat.AgentHelper` | existing names retained |

The installer reuses an already active TermWrap dependency without replacing it or restarting Terminal Services. Windows RDP/Terminal Services is still a shared operating-system component. Do not roll back or remove that component while either app depends on it. agent-seat does not change SeatStream's service, gaming devices, friend seats, or Sunshine instances.

## Use with an AI

The bundled CLI is sufficient; MCP is optional. Both use the same seat control API and protections.

```powershell
$cli = "$env:ProgramFiles\agent-seat\cli\agent-seat.exe"
& $cli computer start --seat agent
& $cli computer begin --seat agent
& $cli computer guide
```

`begin` returns a new context and screenshot path. Have the AI open that image, then use `observe --context <id>` and batched `act` commands. Unchanged screens can return compact text; changed screens include an image or crop. Actual token savings depend on the model and the task. The local human viewer does not send frames to a model.

See [CLI workflow](docs/AGENT-USAGE.md), [optional MCP setup](docs/AGENT-MCP.md), and [security model](SECURITY.md).

## Build from source

Developers need Git, .NET 8 SDK, and Visual Studio/Build Tools with **Desktop development with C++** and Windows SDK. Node.js is needed only to run the browser input tests.

```powershell
git clone https://github.com/aksmfosef11/agent-seat.git
cd agent-seat
.\scripts\Build-TermWrap.ps1
dotnet test .\AgentSeat.sln -c Release
node --test .\tests\ui\remote-control.test.js
.\scripts\Package-Release.ps1
```

The packaging script builds a self-contained `win-x64` release, includes third-party notices, generates a per-file manifest and ZIP checksum, and refuses unexpected secrets or game streaming binaries. The .NET runtime is pinned to `8.0.31` for this preview; maintainers should review supported runtime updates for future releases.

For a faster developer-only publish, use `scripts\Publish.ps1`; installing a release needs the TermWrap dependency too. Binaries, local credentials, logs and build directories are ignored by Git. Upload binaries as release assets, not source commits. See [release procedure](docs/RELEASING.md).

## License

MIT for this project's source. Bundled dependencies retain their licenses: [third-party notices](THIRD_PARTY_NOTICES.md). This project contains no proprietary Duo binaries and does not bundle Windows system DLLs.
