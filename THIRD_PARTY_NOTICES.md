# Third-party notices

The agent-seat release contains this project's MIT source and the following separately licensed dependencies. Sunshine, Steam, MinHook/AppCompat, Moonlight and gamepad drivers are excluded from the release.

## TermWrap, Zydis, Zycore

- [DuoStream/TermWrap](https://github.com/DuoStream/TermWrap), MIT, pinned commit `0da3ac901ab04509d608285caa403a8d1f14f12c`.
- [zyantific/zydis](https://github.com/zyantific/zydis), MIT, pinned submodule `938b5158fd7db5043f88285b23470c8b3b02108a`.
- [zyantific/zycore-c](https://github.com/zyantific/zycore-c), MIT, pinned submodule `75a36c45ae1ad382b0f4e0ede0af84c11ee69928`.

`scripts/Build-TermWrap.ps1` verifies the pinned checkout and builds the x64 dependency. The release includes `TermWrap.dll`, the three unmodified upstream licenses, and hash/source provenance under `artifacts/termwrap`. No proprietary Duo components, Endp binary, or Windows `termsrv.dll` are distributed. TermWrap is an unsupported modification to Windows client session behavior, separate from the agent-seat managed application.

## Microsoft .NET

The self-contained release includes .NET, ASP.NET Core and Windows Desktop runtime `8.0.31`. Original license and third-party notice files supplied by their NuGet runtime packs are included under `licenses/`. The ASP.NET license is additionally taken from the runtime pack's declared source commit, and WPF/WinForms third-party notices from their matching release tags; `licenses/sources.json` records those URLs. Project references also use `Microsoft.Extensions.Hosting.WindowsServices` 8.0.1 and `System.Text.Json` 8.0.6 under Microsoft's published MIT terms. Exact restore results are recorded in `obj/project.assets.json` during builds.

Test dependencies (xUnit and Microsoft.NET.Test.Sdk) are not included in the end-user release.

Some managed source types retain streaming contracts inherited from the original SeatStream code. In agent-seat the service registers a disabled streaming implementation and does not build, launch or package gaming runtimes.
