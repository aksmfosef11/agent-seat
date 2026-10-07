# 安装与恢复

[English](../en/INSTALL.md) · [한국어](../INSTALL.md) · [简体中文](INSTALL.md)

预览安装器面向 Windows 11 Pro/Enterprise/Education x64，不支持 Home、ARM64 或 Windows Server/RDS 自动安装。请使用当前登录的所有者账户及管理员权限。发行 ZIP 已包含 .NET 运行时。安装器消息及原始诊断使用英语。

## 引导安装

下载 [Install-AgentSeat.cmd](https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.2/Install-AgentSeat.cmd) 后双击，或在 64 位 Windows PowerShell 中运行：

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.2/Get-AgentSeat.ps1')))
```

下载固定的 0.9.2 版本，验证 ZIP 大小、SHA-256 校验和及 GitHub 摘要，检查压缩路径和解压文件清单后启动安装。查看计划，输入 INSTALL 接受非官方 TermWrap 修改，再使用同一 Windows 所有者账户确认 UAC。完成后由普通所有者进程打开只读查看器。使用其他管理员账户授权会在安装前停止。

引导消息使用 Windows UI 语言（英语、韩语或简体中文），也可指定 `-Language zh`、`en` 或 `ko`。详细后端诊断保留英语。Windows 安全提示由用户处理。预览脚本没有代码签名，运行前请检查源码及下载来源。启动器只设置当前进程执行策略，不修改系统或组织策略。

仅下载验证，不安装、不请求管理员权限：

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.2/Get-AgentSeat.ps1'))) -DownloadOnly
```

文件保留在 `%LOCALAPPDATA%\agent-seat\Downloads\<唯一文件夹>`。解压后运行 `Setup-Seat.ps1 -Plan` 仅查看计划；`-NoOpen` 禁止安装后自动打开查看器。正常席位会被复用；安装中断时使用同一账户和已保存密码继续修复。现有服务和 CLI 程序保留，修复所需的锚点安装到版本目录，并修正其计划任务。

离线安装请下载 ZIP 和 SHA256SUMS.txt，用 `Get-FileHash .\agent-seat-0.9.2-win-x64.zip -Algorithm SHA256` 对比，解压后双击其中的 Install-AgentSeat.cmd，无需再次下载。如已验证的脚本被阻止，请只对所需文件使用 `Unblock-File`，保留组织策略。

自动化仍可在管理员 PowerShell 中使用底层安装器：

```powershell
.\Install.ps1
.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch
```

它创建专用服务、标准账户、RDP 锚点、授权列表及受保护令牌。密码在创建账户前通过 DPAPI 保存，经标准输入传递，并设置为永不过期。RDP 端口使用 Windows 监听器配置。已有 TermWrap 会被复用，不会替换或重启 Terminal Services。每个席位使用独立的 Windows 账户。

## 查看器和语言

查看器默认只读，可见时约每 600 毫秒刷新。先停止 AI，再启用**手动操作**。支持单击、双击、右键、拖动、滚轮及普通按键。输入法文本请通过文本框发送；浏览器拦截的组合键请使用快捷键按钮。所有者暂停席位后，捕获屏幕和手动输入都会被阻止。

可以在仪表板或查看器中选择英语、韩语或简体中文。首次访问使用浏览器首选语言，手动选择会保存在本地。切换语言不会重新加载页面或清空文本草稿，也不改变 Windows 和应用的语言。查看器授权 30 分钟后过期，请重新运行 `computer view`。人工查看不会调用模型 API。

## 添加席位

使用新的 ID 和账户名。现有 agent-seat 服务会被复用，只添加席位而不更新程序文件：

```powershell
.\Install.ps1 -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2' -Apply -IAcceptUnsupportedWindowsClientPatch
```

属于其他席位或安装的账户及 ID 会被拒绝。同一受管理席位可以修复而不重置密码。此预览版没有服务或 CLI 更新器；替换这些程序前请检查备份流程。

## 安装中断

使用同一席位 ID、账户名和安装时的 Windows 所有者重新运行安装器。受保护的安装记录保存账户 SID 和未完成步骤。安装器验证身份并恢复加密密码，再修复锚点、RDP 端口和计划任务。凭据缺失、账户被删除或替换、任务属于其他所有者时会停止供检查。不会重置现有密码、删除账户或自动回滚共享 RDP。可用 `Get-Service agent-seat`、`Get-ScheduledTask -TaskName 'agent-seat RDP Anchor - *'` 和 CLI `computer status` 检查状态。

某些 Windows 版本会在首次登录时显示隐私或初始设置，请在席位查看器中完成。如果没有会话连接，请用安装时的所有者账户检查锚点 `status --seat agent`。配置和加密凭据位于 `%ProgramData%\agent-seat\RdpAnchors\<owner SID>`。

重启后服务会自动启动。登录安装时的 Windows 所有者账户后，运行 `computer start --seat agent` 或点击查看器的**启动会话**。锚点使用该所有者的交互式登录令牌，不设置 Windows 自动登录或席位自动启动。所有者注销后锚点关闭。修复和重启状态检查已在模拟环境通过；真实新机器安装及重启仍待验证。

## 仅停止或移除 agent-seat

先停止 AI 并保存文件。`computer close-seat --seat agent --keep-files` 只注销 AI 会话。管理员命令 `Stop-Service agent-seat` 只停止新服务。不要通过停止 TermService 或回滚共享 TermWrap 来停止此应用。

移除时只删除 `agent-seat` 服务及 `agent-seat RDP Anchor - …` 任务，再检查独立应用、数据目录和创建的账户。不会自动删除用户配置文件或工作文件。仅清理为 agent-seat 创建的账户和文件。

## 共享 RDP 恢复

首次应用 TermWrap 时，`Install-MultiSession.ps1` 会将原注册表配置备份到 `%ProgramData%\agent-seat\backups`。如果监听未恢复，请使用本次安装对应的备份执行 `scripts\Restore-MultiSession.ps1 -BackupFile <backup-file> -Apply`。

回滚共享 RDP 与移除 agent-seat 是不同操作。恢复前请保存并关闭受影响的会话，检查所有依赖此组件的应用。Windows 更新可能改变兼容性；此预览版没有在所有电脑及 Windows 版本上验证。
