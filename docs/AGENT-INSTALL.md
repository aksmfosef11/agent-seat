# AI가 직접 설치하기

[English](en/AGENT-INSTALL.md) · [한국어](AGENT-INSTALL.md) · [简体中文](zh-CN/AGENT-INSTALL.md)

사용자가 AI에게 "agent-seat를 설치해서 써 줘"라고 맡길 때 AI가 따를 순서입니다. 설치에는 Windows 관리자 권한과, 비공식 TermWrap 수정에 대한 사용자의 동의가 필요합니다. 이 둘은 AI가 우회하거나 대신 결정할 수 없습니다. 설치가 끝나면 [CLI 사용 안내](AGENT-USAGE.md)로 좌석을 사용합니다.

## 시작 전 확인

- Windows 11 Pro/Enterprise/Education x64이고 **64비트 Windows PowerShell**이어야 합니다. Home, ARM64, Windows Server는 지원하지 않습니다.
- 현재 Windows 사용자(소유자)가 관리자 계정이어야 하고, 그 사용자의 대화형 로그인 세션에서 실행해야 합니다. SYSTEM, 서비스 세션, 다른 계정의 관리자 자격 증명으로는 설치되지 않습니다.
- 좌석 ID(기본 `agent`)와 AI용 Windows 계정 이름(기본 `agent-seat-user`)을 사용자에게 확인하세요. 계정은 설치가 새로 만듭니다. agent-seat가 만들지 않은 기존 계정이나 다른 좌석의 ID/계정은 거절됩니다. 좌석을 더 추가할 때는 새 ID와 새 계정 이름을 씁니다.

## 1. 관리자 권한 확인

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

- `True`: 이미 관리자 권한입니다. 승인 창 없이 2단계로 진행합니다.
- `False`: 관리자 권한이 없습니다. 사용자에게 아래 중 하나를 요청하고, 사용자가 답할 때까지 기다리세요.
  - **권장** — "PowerShell을 **관리자 권한으로 실행**한 뒤, 그 안에서 저를 다시 시작해 주세요." 관리자 권한의 AI는 시스템 전체를 바꿀 수 있으므로, 설치가 끝나면 일반 권한 터미널로 돌아가 달라고 함께 안내합니다.
  - "설치 명령을 지금 실행합니다. 화면에 Windows 승인(UAC) 창이 뜨면 **예**를 눌러 주세요." 설치 스크립트가 직접 관리자 권한을 요청합니다. 승인 창은 다른 창 뒤에 가려지거나 작업 표시줄에서 깜박일 수 있고, 같은 소유자 계정으로 승인해야 합니다. 승인 창은 AI가 볼 수도 누를 수도 없습니다.

UAC를 AI가 직접 누르려고 하지 마세요. UAC·실행 정책 같은 시스템 보안 설정을 바꾸거나, 다른 계정·SYSTEM으로 실행하는 방법을 찾지도 마세요.

## 2. 패키지를 내려받고 계획 확인 (변경 없음)

```powershell
$package = & ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.2/Get-AgentSeat.ps1'))) -DownloadOnly
& "$package\Setup-Seat.ps1" -Plan -NoOpen
```

릴리스 ZIP의 크기, SHA-256 체크섬, GitHub 자산 해시와 압축 해제 경로를 검증한 뒤 계획만 출력합니다. 시스템은 바뀌지 않습니다. 출력을 읽고 사용자에게 아래를 알려 **명시적인 동의**를 받으세요.

- 새 Windows 계정(`agent-seat-user` 등), `agent-seat` 서비스, 숨겨진 로컬 RDP 앵커가 만들어집니다.
- 동시 세션에는 비공식 TermWrap 수정이 쓰입니다. 이미 설치된 TermWrap이 있으면 교체나 TermService 재시작 없이 재사용합니다. 처음 적용할 때는 RDP 세션이 끊길 수 있고 Windows 업데이트가 호환성에 영향을 줄 수 있습니다.

사용자가 "동의한다"고 답하기 전에는 다음 단계로 가지 마세요. 설치 스크립트가 묻는 `INSTALL` 입력을 AI가 대신 입력하지도 마세요.

## 3. 설치

사용자가 동의한 뒤에만 `-IAcceptUnsupportedWindowsClientPatch`를 붙입니다.

```powershell
& "$package\Setup-Seat.ps1" -NoOpen -IAcceptUnsupportedWindowsClientPatch
# 다른 좌석이나 계정 이름: -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2'
```

- 관리자 권한이면 현재 창에서 바로 진행합니다.
- 관리자 권한이 아니면 새 관리자 창이 열리고 승인을 기다립니다. 사용자에게 승인 창을 확인해 달라고 하세요. 설치는 몇 분 걸릴 수 있고, 새 계정의 첫 세션 연결을 최대 90초 기다립니다.
- 관리자 창의 오류 메시지는 AI에게 전달되지 않고 "Setup did not complete"만 보입니다. 실패하면 사용자에게 그 창의 메시지를 알려 달라고 요청하세요.
- 설치가 이미 실행 중이면 `Another agent-seat installation is running.`이 나옵니다. 끝날 때까지 기다리세요.

## 4. 확인

```powershell
$cli = "$env:ProgramFiles\agent-seat\cli\agent-seat.exe"
Get-Service agent-seat                  # Running
& $cli list                             # 좌석이 online
& $cli computer status --seat agent     # "sessionAvailable": true
& $cli computer begin --seat agent      # screenshot.path 이미지를 열어 좌석 화면 확인
```

네 가지가 모두 맞으면 설치가 끝난 것입니다. 이후에는 [CLI 사용 안내](AGENT-USAGE.md)를 따릅니다.

## 실패했을 때

- `No live seat session after 90 seconds`: 새 Windows 계정의 첫 로그인이 느릴 수 있습니다. `computer status`로 세션이 실제로 있는지 확인하세요. 같은 좌석 ID와 계정 이름으로 3단계 명령을 다시 실행하면 저장된 계정과 비밀번호를 유지한 채 중단된 단계부터 이어집니다.
- 사용자가 승인 창을 거절하거나 닫았다면 이유를 묻고 다시 요청하세요. 몰래 다른 방법으로 권한을 얻으려 하지 마세요.
- 다음은 하지 마세요: 비밀번호 재설정, 계정 삭제, 토큰이나 승인 목록 직접 수정, TermService 중지, TermWrap 롤백, 계정 이름 바꿔 반복 시도. "계정이 이미 있다" 같은 거절 메시지는 그대로 사용자에게 전달하고 판단을 맡기세요. 자세한 복구는 [설치·복구 안내](INSTALL.md)를 참고하세요.

## 끝난 뒤

- 사용자에게 좌석 ID, Windows 계정 이름, 화면을 보는 방법(`computer view --seat agent`, 읽기 전용으로 열림)을 알려 줍니다.
- 관리자 권한 터미널에서 설치했다면 일반 권한 터미널로 돌아가 달라고 안내합니다.
- 재부팅이나 로그아웃 뒤에는 소유자가 다시 로그인해 `computer start --seat agent`를 실행해야 합니다.
- MCP 등록은 선택 사항입니다. [MCP 설정 안내](AGENT-MCP.md)를 보여 주고, AI 클라이언트 설정은 사용자의 동의를 받은 뒤에만 바꿉니다.
