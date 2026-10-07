# 선택 사항: stdio MCP

[English](en/AGENT-MCP.md) · [한국어](AGENT-MCP.md) · [简体中文](zh-CN/AGENT-MCP.md)

MCP는 필수가 아닙니다. `agent-seat computer mcp`는 이미지와 UI 텍스트를 도구 결과로 직접 반환하는 stdio 서버입니다. CLI와 같은 설치 서비스·보호된 토큰·관리자 승인 좌석을 사용합니다. MCP 연결 자체에는 별도 모델 API 키가 필요하지 않습니다.

클라이언트가 지원하는 MCP 설정에 아래 실행 파일과 인자를 등록하세요. 계정이 읽을 수 있는 토큰 파일 경로만 지정하며 토큰 문자열은 복사하지 않습니다. agent-seat 항목을 새로 추가하고 다른 MCP 서버 설정은 유지하세요.

```toml
[mcp_servers.agent_seat_computer]
command = 'C:\Program Files\agent-seat\cli\agent-seat.exe'
args = ["computer", "mcp", "--seat", "agent"]
tool_timeout_sec = 180

[mcp_servers.agent_seat_computer.env]
AGENTSEAT_AGENT_TOKEN_FILE = 'C:\ProgramData\agent-seat\agent-token.txt'
```

MCP 도구는 `seat_status`, `seat_start`, `seat_observe`, `seat_act`입니다. 같은 좌석의 새 AI 대화에는 새 MCP 연결을 사용합니다. 첫 관찰은 전체 이미지를 반환합니다. 후속 `image="auto"` 관찰은 UI 텍스트를 우선 사용하고 필요한 경우 이미지/crop을 전달합니다. 그림, 캔버스, 색상·배치 검증에는 `image="always"`를 사용하세요. inline image가 제공되면 파일을 별도로 다시 열지 않습니다.

실패/취소는 일부 입력이 실행된 상태일 수 있습니다. 배치를 재전송하지 말고 먼저 다시 관찰하세요. paused/stopped 상태에서는 소유자에게 맡기고 멈추세요. 소유자의 화면, 토큰, 승인 목록을 다른 방법으로 탐색하거나 수정하지 않습니다.

`observationUsage`는 관찰/이미지 개수와 픽셀 수를 보여주며 실제 모델의 청구 토큰 수는 아닙니다. 비용은 모델, 이미지 크기/detail, 도구 호출 횟수와 앱의 UI 텍스트 지원에 따라 달라집니다. JPEG 파일 크기가 줄어든다고 이미지 토큰도 같은 비율로 줄지는 않습니다.
