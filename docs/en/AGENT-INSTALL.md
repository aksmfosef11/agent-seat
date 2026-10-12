# Installing agent-seat by AI

[English](AGENT-INSTALL.md) · [한국어](../AGENT-INSTALL.md) · [简体中文](../zh-CN/AGENT-INSTALL.md)

Follow this order when a user asks an AI to "install agent-seat and use it". Installation needs Windows administrator access and the user's consent to an unsupported TermWrap modification. The AI must not bypass or decide either one. After installation, use the seat with the [CLI workflow](AGENT-USAGE.md).

## Before you start

- Windows 11 Pro/Enterprise/Education x64 and **64-bit Windows PowerShell** are required. Windows Home, ARM64 and Windows Server are unsupported.
- The current Windows user (the owner) must be an administrator account and must run this from that user's interactive sign-in session. SYSTEM, service sessions and administrator credentials of another account do not work.
- Ask the user for the seat ID (default `agent`) and the AI's Windows account name (default `agent-seat-user`). Setup creates the account. Existing accounts that agent-seat did not create, and IDs or accounts belonging to another seat, are refused. To add another seat, use a new ID and a new account name.

## 1. Check administrator access

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

- `True`: you already have administrator access. Continue to step 2; no approval prompt will appear.
- `False`: you do not. Ask the user for one of the following and wait for their reply.
  - **Recommended** — "Please open PowerShell **as administrator** and restart me inside it." An AI with administrator rights can change the whole system, so also ask the user to return to an ordinary terminal once installation is finished.
  - "I am about to run the installer. When the Windows approval (UAC) prompt appears, please click **Yes**." The setup script requests administrator access itself. The prompt can be hidden behind other windows or blink on the taskbar, and it must be approved with the same owner account. The AI can neither see nor click it.

Do not try to click UAC yourself. Do not change system security settings such as UAC or execution policy, and do not look for a way to run as another account or SYSTEM.

## 2. Download the package and review the plan (no changes)

```powershell
$package = & ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.2/Get-AgentSeat.ps1'))) -DownloadOnly
& "$package\Setup-Seat.ps1" -Plan -NoOpen
```

This verifies the release ZIP's size, SHA-256 checksum, GitHub asset digest and extraction paths, then prints the plan only. Nothing on the system changes. Read the output and tell the user the following, then get **explicit consent**.

- A new Windows account (such as `agent-seat-user`), the `agent-seat` service and a hidden local RDP anchor will be created.
- Concurrent sessions use an unsupported TermWrap modification. An already installed TermWrap is reused without replacing it or restarting TermService. A first application can disconnect RDP sessions, and Windows updates can affect compatibility.

Do not continue until the user says they agree. Do not type the `INSTALL` confirmation that the setup script asks for on the user's behalf.

## 3. Install

Add `-IAcceptUnsupportedWindowsClientPatch` only after the user has agreed.

```powershell
& "$package\Setup-Seat.ps1" -NoOpen -IAcceptUnsupportedWindowsClientPatch
# For another seat or account name: -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2'
```

- With administrator access, setup continues in the current window.
- Without it, a new administrator window opens and waits for approval. Ask the user to check for the approval prompt. Installation can take a few minutes, and setup waits up to 90 seconds for the new account's first session.
- Error messages in the administrator window do not reach the AI; you only see "Setup did not complete". If it fails, ask the user to tell you the message shown in that window.
- If another installation is running you will see `Another agent-seat installation is running.` Wait for it to finish.

## 4. Verify

```powershell
$cli = "$env:ProgramFiles\agent-seat\cli\agent-seat.exe"
Get-Service agent-seat                  # Running
& $cli list                             # the seat is online
& $cli computer status --seat agent     # "sessionAvailable": true
& $cli computer begin --seat agent      # open screenshot.path and look at the seat screen
```

When all four are right, installation is complete. Continue with the [CLI workflow](AGENT-USAGE.md).

## If it fails

- `No live seat session after 90 seconds`: the new Windows account's first sign-in can be slow. Check with `computer status` whether the session really exists. Running the step 3 command again with the same seat ID and account name keeps the saved account and password and resumes at the interrupted step.
- If the user declined or closed the approval prompt, ask why and request it again. Do not try to obtain privileges another way.
- Do not reset passwords, delete accounts, edit the token or approval list directly, stop TermService, roll back TermWrap, or retry under a different account name. Pass refusals such as "the account already exists" to the user as they are and leave the decision to them. See [installation and recovery](INSTALL.md) for details.

## When finished

- Tell the user the seat ID, the Windows account name and how to watch the screen (`computer view --seat agent`, which opens read-only).
- If you installed from an administrator terminal, ask the user to go back to an ordinary terminal.
- After a reboot or sign-out, the owner must sign in again and run `computer start --seat agent`.
- MCP registration is optional. Show the [MCP setup](AGENT-MCP.md) and change the AI client's configuration only with the user's consent.
