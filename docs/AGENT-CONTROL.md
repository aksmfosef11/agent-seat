# Agent control architecture

The loopback service on port 38399 stores seat definitions under `%ProgramData%\agent-seat`. Each seat identifies a dedicated standard Windows account. The installer creates a hidden local RDP anchor task owned by the interactive installing user. The Windows service is `agent-seat`, installed separately from SeatStream.

The agent API checks a protected bearer token and an administrator-controlled seat ID/account allowlist. It refuses the physical console. An `AgentSeat.AgentHelper` process runs within the seat's own WTS session, captures its desktop and injects input there. A named pipe specific to agent-seat verifies the peer process and permits only the authorized service identity. Session IDs, executable paths and process identity are checked before a helper is used or terminated.

Both CLI and MCP use this API. CLI contexts and individual MCP connections own their observation baseline, so human preview frames do not replace an AI's baseline. UI Automation text reads run in a bounded, read-only child process; hung providers time out. Missing UI text falls back to images. Changes can be reported as crops. Actual model token usage is outside this local service.

The browser viewer redeems a one-use ticket into a seat-specific cookie, captures JPEG frames while visible, and sends manual input to the same action endpoint. The client serializes input and cancels unsent actions when closed or disarmed. The owner's pause/kill switch applies equally to AI and browser input. Manual control is opt-in; this preview requires users to stop AI work before taking over.

Game streaming is registered as a disabled implementation. The service does not register the gaming auto-start or virtual gamepad isolation worker. Release packaging builds only service, CLI, RDP anchor and agent helper; it excludes Sunshine, Steam launchers, native AppCompat and device drivers.

Windows Terminal Services and any installed TermWrap modification are shared host components. agent-seat reuses existing TermWrap without replacing it or restarting that service. Application/service/data/token/task/helper names and the default Windows seat user are distinct from SeatStream. Shared RDP recovery remains a separate host operation; see [INSTALL.md](INSTALL.md).
