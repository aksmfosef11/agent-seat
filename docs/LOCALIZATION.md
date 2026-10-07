# Localization

Supported web languages: `en` (English), `ko` (Korean), `zh` (Simplified Chinese). Chinese is rendered with HTML language tag `zh-Hans`. Region variants such as `ko-KR` and `zh-CN` resolve to the corresponding supported language; unsupported preferences fall back to English.

The precedence on initial load is an optional `?lang=` parameter, a saved `agent-seat-language` browser preference, then `navigator.languages`. An explicit dropdown choice is saved when storage is available and removes the initial `lang` query parameter so it remains effective on reload. Blocked local storage does not prevent switching within the page.

All UI messages live in `src/AgentSeat.Service/wwwroot/i18n.js`. Static text uses `data-i18n`; alternative text, placeholders and accessible labels use matching `data-i18n-*` attributes. Dynamic status messages use stable API codes and fields. User-provided names are inserted as text, never HTML. Language changes redraw cached text without a reload, sending input or resetting the open viewer/text draft.

Dates and counts use `Intl` with the selected locale. Shortcuts, commands, identifiers, API fields and original backend diagnostics are retained for interoperability. Host warnings include expandable original diagnostics. Unknown future checks keep their original messages.

For a new translation, add its native language label and a complete dictionary with the same keys and named placeholders. Run `npm test`, then check the dashboard and open viewer in a browser, including a narrow viewport, a language change with drafted text, persistence after reload and error messages. Update the matching README and install/CLI/MCP guides too.

Localized overview files are `README.md`, `README.ko.md` and `README.zh-CN.md`. Korean guides remain under `docs/`; English and Simplified Chinese guides live in `docs/en/` and `docs/zh-CN/`. They are included in release ZIPs alongside the shared concept banner. These guides describe the same support limits and recovery requirements.

The guided installer (`Get-AgentSeat.ps1` and `Setup-Seat.ps1`) also supports `-Language en|ko|zh`, or automatic Windows UI language selection. Detailed low-level installer diagnostics retain their original English text. Keep `Setup-Seat.ps1` in UTF-8 with BOM for Windows PowerShell 5.1. The remotely evaluated `Get-AgentSeat.ps1` must remain ASCII without BOM: its JSON Unicode escapes preserve translated messages even when Windows PowerShell decodes HTTP text using a legacy encoding.
