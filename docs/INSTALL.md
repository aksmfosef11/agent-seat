# 설치와 복구

[English](en/INSTALL.md) · [한국어](INSTALL.md) · [简体中文](zh-CN/INSTALL.md)

이 설치기는 Windows 11 Pro/Enterprise/Education x64를 대상으로 합니다. Home, ARM64, Windows Server/RDS용 자동 설치는 제공하지 않습니다. 관리자 권한과 현재 로그인한 소유자의 Windows 계정이 필요합니다. 릴리스 ZIP은 .NET 런타임을 포함합니다.

## 안내에 따라 설치하기

[Install-AgentSeat.cmd](https://github.com/aksmfosef11/agent-seat/releases/download/v0.9.1/Install-AgentSeat.cmd)를 내려받아 더블 클릭하거나, 64비트 Windows PowerShell에서 한 줄을 실행하세요.

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.1/Get-AgentSeat.ps1')))
```

0.9.1 배포본을 내려받아 ZIP 크기·SHA-256 체크섬·GitHub 파일 해시를 확인합니다. 압축 내부 경로와 압축 해제된 파일 목록도 검증한 뒤 설치를 시작합니다. 계획을 확인하고 INSTALL을 입력해 비공식 TermWrap 수정에 동의한 뒤, 같은 Windows 소유자 계정으로 관리자 승인창을 확인하세요. 완료 후 일반 소유자 프로세스에서 읽기 전용 화면을 엽니다. 다른 관리자 계정으로 승인하면 설치 전에 중단합니다.

설치 안내는 Windows UI 언어에 따라 한국어·영어·중국어 간체로 표시됩니다. `-Language ko`, `en`, `zh`로 지정할 수도 있습니다. 상세 백엔드 진단은 영어를 유지합니다. Windows 보안 승인창은 사용자가 처리합니다. 시험판 스크립트에는 코드 서명이 없으므로 실행 전 소스와 다운로드 출처를 확인하세요. 실행 정책은 해당 프로세스에만 적용하며 시스템·조직 정책을 변경하지 않습니다.

관리자 권한이나 설치 없이 내려받고 검증만 하려면:

```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/aksmfosef11/agent-seat/v0.9.1/Get-AgentSeat.ps1'))) -DownloadOnly
```

파일은 `%LOCALAPPDATA%\agent-seat\Downloads\<고유 폴더>`에 보관됩니다. ZIP에서 `Setup-Seat.ps1 -Plan`은 변경 없이 계획만 확인하고, `-NoOpen`은 설치 후 화면을 자동으로 열지 않습니다. 기존 좌석이 있으면 재사용하며 서비스 실행 파일은 업데이트하지 않습니다. 중지되거나 설치가 중간에 끝난 서비스는 임의로 교체하지 않습니다.

오프라인 설치는 ZIP과 SHA256SUMS.txt를 받아 `Get-FileHash .\agent-seat-0.9.1-win-x64.zip -Algorithm SHA256`으로 비교한 뒤 압축을 풀고 안의 Install-AgentSeat.cmd를 실행하세요. 추가 다운로드는 없습니다. 검증된 스크립트가 차단되면 필요한 파일에만 `Unblock-File`을 적용하고 조직 정책은 유지하세요.

자동화용 기존 설치 스크립트도 관리자 PowerShell에서 사용할 수 있습니다.

```powershell
.\Install.ps1
.\Install.ps1 -Apply -IAcceptUnsupportedWindowsClientPatch
```

전용 서비스·표준 계정·로컬 RDP 앵커·승인 목록·보호된 토큰을 만듭니다. 생성한 비밀번호는 표준 입력으로 전달하고 DPAPI로 저장하며 화면이나 명령 인자에 표시하지 않습니다. 기존 TermWrap은 교체하거나 Terminal Services를 재시작하지 않고 재사용합니다. 좌석마다 별도 Windows 계정을 사용하세요.

## 화면 확인과 직접 조작

보기 창은 읽기 전용으로 열립니다. 화면은 창이 보일 때 약 600ms마다 갱신되고, 입력이 실행 중일 때는 중복 캡처를 피합니다. 세션이 꺼져 있으면 창의 **세션 시작** 버튼을 누르세요.

AI 작업을 멈춘 뒤 **직접 조작**을 켜고 화면을 클릭합니다. 클릭·더블 클릭·오른쪽 클릭·드래그·휠·일반 키 입력을 사용할 수 있습니다. 한글 IME는 아래 텍스트 입력칸에 작성한 뒤 **텍스트 보내기**를 누르세요. 브라우저가 먼저 처리하는 조합은 단축키 버튼을 이용하세요. 일시정지 상태에서는 화면 캡처와 직접 조작 모두 차단됩니다. 재개 후 사용하세요.

토큰을 복사하지 않는 `computer view` 세션은 30분 후 만료됩니다. 다시 같은 명령을 실행하면 됩니다. 화면 보기 자체는 AI API를 호출하지 않으며 모델 토큰을 사용하지 않습니다.

대시보드와 보기 창의 언어 선택에서 한국어·영어·중국어 간체를 바꿀 수 있습니다. 첫 방문에는 브라우저 언어를 따르고, 직접 선택한 언어는 브라우저에 저장됩니다. 화면이나 작성 중인 텍스트는 초기화되지 않습니다. 좌석 안의 Windows와 앱 언어는 별도이며 설치기 메시지·진단 원문은 영어를 사용합니다.

## 추가 좌석

관리자 PowerShell에서 릴리스 폴더의 설치기를 새 ID와 새 사용자 이름으로 실행합니다. 실행 중인 agent-seat 서비스나 이미 만든 좌석은 업데이트하지 않고 새 좌석만 추가합니다.

```powershell
.\Install.ps1 -SeatId agent2 -UserName agent-seat-user2 -DisplayName 'AI Desktop 2' -Apply -IAcceptUnsupportedWindowsClientPatch
```

기존 계정이나 ID를 지정하면 비밀번호를 바꾸거나 덮어쓰지 않고 중단합니다. 이 버전에는 인플레이스 업데이터가 없습니다. 새 버전의 파일을 기존 서비스 폴더에 임의로 덮어쓰기 전에 서비스·앵커·설정 백업 절차를 검토하세요.

## 설치가 중간에 실패한 경우

자동으로 기존 계정을 삭제하거나 공유 RDP를 되돌리지 않습니다. `Get-Service agent-seat`, `Get-ScheduledTask -TaskName 'agent-seat RDP Anchor - *'`, CLI의 `computer status`로 현재 상태를 확인하세요. 생성된 계정 이름으로 재실행하면 의도적으로 중단됩니다. 상태를 확인한 후 새 이름으로 좌석을 추가하거나 생성된 좌석의 앵커를 복구하세요.

첫 로그온의 개인정보/초기 설정 화면이 나타나는 Windows 빌드가 있습니다. 이때 숨은 앵커는 연결됐어도 일반 앱 작업이 준비되지 않을 수 있습니다. 좌석 보기 창에서 초기 설정을 완료하세요. 연결이 없으면 앵커의 `status --seat agent`로 DPAPI 소유자와 로그를 확인하세요. 소유자가 로그아웃하면 숨은 앵커가 종료됩니다. 다시 로그인한 뒤 CLI `computer start`로 시작하세요.

## agent-seat만 중지하기

먼저 AI를 중지하고 좌석에서 열린 파일을 저장하세요. `computer close-seat --seat agent --keep-files`는 해당 AI 세션만 로그오프합니다. 관리자 PowerShell의 `Stop-Service agent-seat`는 새 서비스만 중지합니다. 공유 TermWrap을 되돌리거나 TermService를 중지하지 마세요.

앱 제거는 agent-seat 서비스와 `agent-seat RDP Anchor - …` 작업만 제거한 후 별도 앱/데이터 폴더와 자신이 만든 계정을 검토하여 정리합니다. Windows 사용자 프로필과 작업 파일은 자동 삭제하지 않습니다. agent-seat가 만든 계정과 파일만 정리하세요.

## Windows 원격 세션 패치 복구

새로 TermWrap을 적용할 때 `Install-MultiSession.ps1`은 `%ProgramData%\agent-seat\backups`에 기존 레지스트리 값의 백업을 남깁니다. 적용 후 RDP 리스너가 정상화되지 않으면 즉시 해당 백업으로 복구합니다. 수동 복구는 `scripts\Restore-MultiSession.ps1 -BackupFile <해당 설치의 백업 파일> -Apply`입니다.

공유 TermWrap 복구는 agent-seat 제거와 별개입니다. 다른 RDP 세션이 해당 패치를 사용 중이면 먼저 모든 세션을 저장·종료하고 영향 범위를 확인하세요. 새 Windows 업데이트에서 동작이 바뀔 수 있습니다. 모든 PC/빌드에서의 설치 성공을 보장하는 버전은 아닙니다.
