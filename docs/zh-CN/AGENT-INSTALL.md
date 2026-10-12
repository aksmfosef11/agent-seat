# 由 AI 安装 agent-seat

[English](../en/AGENT-INSTALL.md) · [한국어](../AGENT-INSTALL.md) · [简体中文](AGENT-INSTALL.md)

当用户让 AI“安装 agent-seat 并使用它”时，请按本文顺序操作。安装需要 Windows 管理员权限，并需要用户同意非官方的 TermWrap 修改。AI 不能绕过这两项，也不能替用户做决定。安装完成后，按 [CLI 工作流程](AGENT-USAGE.md)使用席位。

## 开始之前

- 需要 Windows 11 Pro/Enterprise/Education x64 和 **64 位 Windows PowerShell**。不支持 Windows Home、ARM64 和 Windows Server。
- 当前 Windows 用户（所有者）必须是管理员账户，并在该用户的交互式登录会话中运行。SYSTEM、服务会话以及其他账户的管理员凭据都无法完成安装。
- 请向用户确认席位 ID（默认 `agent`）和供 AI 使用的 Windows 账户名（默认 `agent-seat-user`）。账户由安装程序新建。非 agent-seat 创建的现有账户，以及属于其他席位的 ID 或账户，都会被拒绝。添加更多席位时请使用新的 ID 和新的账户名。

## 1. 检查管理员权限

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

- `True`：已有管理员权限。直接进入第 2 步，不会出现授权提示。
- `False`：没有管理员权限。请用户在下面两种方式中选择一种，并等待用户回复。
  - **推荐** —— “请以**管理员身份运行** PowerShell，并在其中重新启动我。”拥有管理员权限的 AI 可以修改整个系统，因此请同时提醒用户在安装完成后回到普通权限的终端。
  - “我现在运行安装命令。当屏幕上出现 Windows 授权（UAC）窗口时，请点击**是**。”安装脚本会自行请求管理员权限。授权窗口可能被其他窗口遮住或在任务栏闪烁，并且必须使用同一所有者账户授权。AI 既看不到也点不了该窗口。

不要试图自己点击 UAC。不要修改 UAC、执行策略等系统安全设置，也不要寻找以其他账户或 SYSTEM 运行的办法。

## 2. 下载安装包并确认计划（不做任何更改）

```powershell
$package = & ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.3/Get-AgentSeat.ps1'))) -DownloadOnly
& "$package\Setup-Seat.ps1" -Plan -NoOpen
```

该命令会验证发布 ZIP 的大小、SHA-256 校验和、GitHub 资源摘要以及解压路径，然后只输出计划，不会更改系统。请阅读输出，向用户说明以下内容，并获得**明确同意**。

- 将创建新的 Windows 账户（如 `agent-seat-user`）、`agent-seat` 服务和隐藏的本地 RDP 锚点。
- 并发会话使用非官方的 TermWrap 修改。如果已安装 TermWrap，会直接复用，不会替换它，也不会重启 TermService。首次应用可能中断 RDP 会话，Windows 更新也可能影响兼容性。

在用户明确表示同意之前，不要继续下一步。安装脚本要求输入的 `INSTALL` 确认，AI 也不能代替用户输入。

## 3. 安装

只有在用户同意之后，才添加 `-IAcceptUnsupportedWindowsClientPatch`。

```powershell
& "$package\Setup-Seat.ps1" -NoOpen -IAcceptUnsupportedWindowsClientPatch
# 添加其他席位或账户名：-SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2'
```

- 已有管理员权限时，安装会在当前窗口中直接进行。
- 没有管理员权限时，会打开新的管理员窗口并等待授权。请用户查看授权窗口。安装可能需要几分钟，并且会最多等待 90 秒让新账户建立第一个会话。
- 管理员窗口中的错误信息不会传给 AI，你只能看到“Setup did not complete”。如果失败，请让用户告诉你那个窗口里显示的信息。
- 如果已有安装正在运行，会显示 `Another agent-seat installation is running.`，请等待其结束。

## 4. 验证

```powershell
$cli = "$env:ProgramFiles\agent-seat\cli\agent-seat.exe"
Get-Service agent-seat                  # Running
& $cli list                             # 席位为 online
& $cli computer status --seat agent     # "sessionAvailable": true
& $cli computer begin --seat agent      # 打开 screenshot.path 的图片，确认席位屏幕
```

四项都正确，即表示安装完成。之后请按 [CLI 工作流程](AGENT-USAGE.md)操作。

## 失败时

- `No live seat session after 90 seconds`：新 Windows 账户的首次登录可能较慢。请用 `computer status` 确认会话是否真的存在。使用相同的席位 ID 和账户名再次运行第 3 步的命令，会保留已保存的账户和密码，并从中断的步骤继续。
- 如果用户拒绝或关闭了授权窗口，请询问原因并再次请求。不要尝试用其他方式获取权限。
- 不要重置密码、删除账户、直接修改令牌或批准列表、停止 TermService、回滚 TermWrap，也不要换账户名反复尝试。对于“账户已存在”之类的拒绝信息，请原样转告用户，由用户决定。详细恢复步骤见[安装与恢复](INSTALL.md)。

## 完成之后

- 告诉用户席位 ID、Windows 账户名，以及查看屏幕的方法（`computer view --seat agent`，以只读方式打开）。
- 如果是在管理员终端中安装的，请提醒用户回到普通权限的终端。
- 重启或注销之后，所有者需要重新登录并运行 `computer start --seat agent`。
- MCP 注册是可选的。请向用户展示 [MCP 设置](AGENT-MCP.md)，只有在用户同意后才修改 AI 客户端的配置。
