# Optional stdio MCP

[English](AGENT-MCP.md) · [한국어](../AGENT-MCP.md) · [简体中文](../zh-CN/AGENT-MCP.md)

MCP is optional. `agent-seat computer mcp` is a stdio server that returns images and UI text directly in tool results. It uses the same installed service, protected token and approved seat as the CLI. Connecting it needs no separate model API key.

Register the executable and arguments in your MCP client's settings. Specify a readable token file path, not its secret contents. Add a separate entry and retain your other MCP servers.

```toml
[mcp_servers.agent_seat_computer]
command = 'C:\Program Files\agent-seat\cli\agent-seat.exe'
args = ["computer", "mcp", "--seat", "agent"]
tool_timeout_sec = 180

[mcp_servers.agent_seat_computer.env]
AGENTSEAT_AGENT_TOKEN_FILE = 'C:\ProgramData\agent-seat\agent-token.txt'
```

Tools are `seat_status`, `seat_start`, `seat_observe` and `seat_act`. Start a fresh MCP connection for a new AI conversation. The first observation returns a full image. Later `image="auto"` observations favor UI text and include images/crops when necessary. Use `image="always"` for visual or layout checks. When an inline image is supplied, do not reopen the same image file separately.

Pass a `handle` to `seat_observe` to read a specific window. Start with `interactiveOnly: true` and `maxDepth: 2` for an overview, then pass a ref marked `canExpand` in `ui.targets` as `rootRef`. Each target's `index` refers to a numbered row in `ui.text`. Use `{"type":"invoke","ref":"…"}` or `{"type":"set_value","ref":"…","text":"…"}` in `seat_act`. The `wait_for` action takes a `windowTitle` or `handle` and optional `role`, `name` and `milliseconds` to wait for UI to appear.

`actionResults` reports each attempted step and its input index. A `disposition.delivery` of `not_delivered` means that step did not dispatch. If it reports `delivery_uncertain` or `delivered_unverified`, observe the UI before taking another action. A failed follow-up UI read is reported separately as `observationError`, and a failed image capture as `screenshotError`.

Failure or cancellation can happen after partial input. Observe before retrying; do not resend the batch automatically. Respect paused/stopped states. Do not inspect or modify the owner's desktop, token or approval list through other means.

`observationUsage` counts observations, images and pixels, not billed model tokens. Actual cost depends on the model, image dimensions/detail, tool calls and UI text support. Smaller JPEG bytes do not imply proportionally fewer image tokens. Web UI language settings do not change MCP tool names or schemas.
