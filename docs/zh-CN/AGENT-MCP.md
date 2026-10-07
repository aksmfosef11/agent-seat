# 可选的 stdio MCP

[English](../en/AGENT-MCP.md) · [한국어](../AGENT-MCP.md) · [简体中文](AGENT-MCP.md)

MCP 不是必需的。`agent-seat computer mcp` 是 stdio 服务，在工具结果中直接返回图像及 UI 文本。它与 CLI 使用相同的已安装服务、受保护令牌和已批准席位。连接本身不需要额外的模型 API 密钥。

在 MCP 客户端设置中注册可执行文件及参数。只指定账户可以读取的令牌文件路径，不要复制令牌内容。添加独立条目，保留其他 MCP 服务器设置。

```toml
[mcp_servers.agent_seat_computer]
command = 'C:\Program Files\agent-seat\cli\agent-seat.exe'
args = ["computer", "mcp", "--seat", "agent"]
tool_timeout_sec = 180

[mcp_servers.agent_seat_computer.env]
AGENTSEAT_AGENT_TOKEN_FILE = 'C:\ProgramData\agent-seat\agent-token.txt'
```

工具包括 `seat_status`、`seat_start`、`seat_observe`、`seat_act`。新 AI 对话应建立新的 MCP 连接。首次观察返回完整图像；后续 `image="auto"` 优先使用 UI 文本，必要时返回图像或区域截图。图形或布局检查请使用 `image="always"`。已提供内联图像时，不必再次打开同一图像文件。

失败或取消可能发生在部分输入已执行之后。重试前先观察，切勿自动重发批次。尊重暂停或停止状态，不要通过其他途径查看或修改所有者桌面、令牌或授权列表。

`observationUsage` 统计观察、图像及像素数量，不是模型计费令牌。成本取决于模型、图像尺寸和 detail、工具调用及 UI 文本支持。JPEG 文件更小不意味着图像令牌同比减少。网页语言设置不改变 MCP 工具名称或结构。
