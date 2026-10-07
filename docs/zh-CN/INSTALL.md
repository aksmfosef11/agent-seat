# 安装与恢复

[English](../en/INSTALL.md) · [한국어](../INSTALL.md) · [简体中文](INSTALL.md)

预览安装器面向 Windows 11 Pro/Enterprise/Education x64，不支持 Home、ARM64 或 Windows Server/RDS 自动安装。请使用当前登录的所有者账户及管理员权限。发行 ZIP 已包含 .NET 运行时。安装器消息及原始诊断使用英语。

## 首次安装

1. 从 [Releases](https://github.com/aksmfosef11/agent-seat/releases) 下载可执行 ZIP 和 SHA256SUMS.txt。将 `Get-FileHash .\agent-seat-0.8.0-win-x64.zip -Algorithm SHA256` 与校验和对比，再解压到 `C:\agent-seat` 等目录。
2. 打开 64 位管理员 Windows PowerShell，进入该目录，运行 `.\Install.ps1` 查看计划。
3. 了解为 Windows 客户端启用并发会话的非官方 TermWrap 修改，再运行 `.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch`。
4. 安装器创建独立服务、标准账户 `agent-seat-user`、隐藏的本地 RDP 锚点任务、授权列表及令牌。生成的密码通过标准输入传递并由 DPAPI 保存，不显示在屏幕或命令参数中。
5. 双击 `View-Seat.cmd`，或使用普通所有者账户执行 `& "$env:ProgramFiles\agent-seat\cli\agent-seat.exe" computer view --seat agent`。

如果下载的脚本被阻止，请先验证文件，再执行 `Unblock-File .\Install.ps1` 和 `Get-ChildItem .\scripts\*.ps1 | Unblock-File`。必要时仅临时调整当前进程的执行策略，保留系统及组织策略。

已安装的 SeatStream、朋友账户、Sunshine 及游戏设备设置保持不变。现有 TermWrap 会被复用，不会替换它或重启 Terminal Services。新默认账户与 `seat-agent` 不同；两个应用不要共用同一个 Windows 席位账户。

## 查看器和语言

查看器默认只读，可见时约每 600 毫秒刷新。先停止 AI，再启用**手动操作**。支持单击、双击、右键、拖动、滚轮及普通按键。输入法文本请通过文本框发送；浏览器拦截的组合键请使用快捷键按钮。所有者暂停席位后，捕获屏幕和手动输入都会被阻止。

可以在仪表板或查看器中选择英语、韩语或简体中文。首次访问使用浏览器首选语言，手动选择会保存在本地。切换语言不会重新加载页面或清空文本草稿，也不改变 Windows 和应用的语言。查看器授权 30 分钟后过期，请重新运行 `computer view`。人工查看不会调用模型 API。

## 添加席位

使用新的 ID 和账户名。现有 agent-seat 服务会被复用，只添加席位而不更新程序文件：

```powershell
.\Install.ps1 -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2' -Apply -IAcceptUnsupportedWindowsClientPatch
```

现有账户或 ID 会被拒绝，不会重置密码。此预览版没有就地更新器；替换已安装文件前请查看服务、锚点及配置备份流程。

## 安装中断

安装器不会自动删除账户或回滚共享 RDP。请检查 `Get-Service agent-seat`、`Get-ScheduledTask -TaskName 'agent-seat RDP Anchor - *'` 和 CLI `computer status`。再次使用已创建的账户名会按设计停止。检查状态后修复该席位的锚点，或使用新名称添加席位。

某些 Windows 版本会在首次登录时显示隐私或初始设置，请在席位查看器中完成。如果没有会话连接，请用安装时的所有者账户检查锚点 `status --seat agent`。配置和加密凭据位于 `%ProgramData%\agent-seat\RdpAnchors\<owner SID>`。所有者注销会关闭锚点；重新登录后运行 `computer start`。重启及重新登录仍需进一步验收测试。

## 仅停止或移除 agent-seat

先停止 AI 并保存文件。`computer close-seat --seat agent --keep-files` 只注销 AI 会话。管理员命令 `Stop-Service agent-seat` 只停止新服务。不要通过停止 TermService 或回滚共享 TermWrap 来停止此应用。

移除时只删除 `agent-seat` 服务及 `agent-seat RDP Anchor - …` 任务，再检查独立应用、数据目录和创建的账户。不会自动删除用户配置文件或工作文件。请确保每个目标与 SeatStream 区分。

## 共享 RDP 恢复

首次应用 TermWrap 时，`Install-MultiSession.ps1` 会将原注册表配置备份到 `%ProgramData%\agent-seat\backups`。如果监听未恢复，请使用本次安装对应的备份执行 `scripts\Restore-MultiSession.ps1 -BackupFile <backup-file> -Apply`。

回滚共享 RDP 与移除 agent-seat 是不同操作。恢复前请保存并关闭受影响的会话，检查所有依赖此组件的应用。Windows 更新可能改变兼容性；此预览版没有在所有电脑及 Windows 版本上验证。
