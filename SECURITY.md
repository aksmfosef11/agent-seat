# Security

agent-seat binds only to loopback and rejects non-loopback Host headers. Do not expose port 38399 through a reverse proxy, port forwarding or remote tunnel. There is no remote browser login in this preview.

The installed Windows service runs as LocalSystem to start an input helper inside an explicitly approved standard user's session. An administrator-controlled allowlist binds the seat ID to its Windows account. Agent requests refuse unapproved seats and the physical console. Separate Windows users provide a desktop/session boundary, not a virtual-machine boundary: applications and kernel services still share the host.

The agent bearer token is stored at `%ProgramData%\agent-seat\agent-token.txt` with SYSTEM, Administrators and the installing owner as readers. Never grant the seat account access to that token. Never commit it, stored RDP credentials, local configuration, screenshots or diagnostic logs. RDP anchor credentials are protected with Windows DPAPI for the interactive owner and stored under `%ProgramData%\agent-seat\RdpAnchors\<owner SID>` with access restricted to that owner, SYSTEM and Administrators.

`computer view` requests a random one-use ticket, valid for 60 seconds, and opens the local browser. Redeeming it sets a seat-scoped HttpOnly, SameSite=Strict cookie valid for at most 30 minutes and redirects to a URL containing only the seat ID. The browser cookie cannot mint another ticket, cannot control another seat, and expires when the service restarts or the bearer token changes. Authentication endpoints and images use `Cache-Control: no-store`. Tickets are secrets too: do not share the transient launch URL or browser history while it is valid. Ticket secrets are stored only as hashes in service memory.

Manual control uses the same checked action endpoint as the AI. It starts disabled, is limited to the focused image and explicit input controls, serializes bounded input, drops unsent input on close/error, and does not retry a failed batch. The owner's pause and kill switch still apply. A request already being executed may finish when the viewer is closed; use the owner's input stop control to terminate input immediately. Stop AI work before taking over; this preview does not provide operator leasing between simultaneous AI and human clients.

The human viewer periodically captures the complete seat screen. Unlike the AI observation path, its images are not password-redacted. Treat the viewer as direct access to that Windows desktop. The existing UI Automation text path masks password controls; screenshot masking cannot reliably cover custom applications.

TermWrap changes a shared Windows system component. The installer requires explicit opt-in, preserves an existing TermWrap installation, and refuses ambiguous coexistence configurations. Recovery must account for every application using that component. Do not run the dependency rollback to uninstall only agent-seat while SeatStream still uses it.

Report suspected vulnerabilities privately to the repository owner before including tokens, personal screenshots or repro logs in a public issue.
