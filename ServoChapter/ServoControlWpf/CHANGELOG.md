# 변경 내역 (CHANGELOG)

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/) 를 따르고, 버전은 [SemVer](https://semver.org/lang/ko/) 입니다.
버전 번호의 단일 진실은 `VERSION` 파일이며, `tools/bump-version.ps1`(또는 `tools/bump_version.sh`)로 올립니다.

## [Unreleased]

## [1.2.0] - 2026-10-27

### Changed
- **연결 방식 정리**: "직접 연결" 은 **실제 Pi(LAN 주소)** 에만 쓰도록 바꿈. `127.0.0.1`·`localhost`·`::1`·`0.0.0.0` 같은 자기 주소를 넣고 [연결] 하면 거부하고 에뮬레이터로 안내(`MainViewModel.IsLoopbackHost`). 지금까지 "직접 연결 127.0.0.1 + WSL 의 fake_pigpiod.py" 로 하던 연습은 사실상 에뮬레이션이었으므로 아래 항목으로 옮김.
- 에뮬레이터 상단 안내: 포트 충돌로 임시 포트를 쓸 때 "그 서버에 붙고 싶으면 에뮬레이터 종류를 'WSL 가짜 Pi' 로" 로 문구 변경.

### Added
- **에뮬레이터 종류** 콤보(연결 방식 "에뮬레이터" 일 때만 표시): **앱 안 가짜 Pi** (기존, `PigpioEmulatorServer`) / **WSL 가짜 Pi (fake_pigpiod.py)** (새로). 후자는 앱이 `wsl.exe --cd tools -e python3 fake_pigpiod.py --port N --bind 0.0.0.0` 으로 리눅스 가짜 pigpiod 를 띄우고 127.0.0.1:N 으로 접속하며, 그 프로세스의 stdout(받은 명령)을 상단에 그대로 보여 줌. 이미 그 포트가 열려 있으면(사용자가 `./run-from-wsl.sh fakepi` 로 띄워 둠) 새로 띄우지 않고 재사용. 앱이 띄운 것은 종료 시 함께 끝내고, 상태 파일 `~/.config/VI/fake_pigpiod.json` 덕분에 위치 기억은 유지 (`Model/WslFakePi.cs`, 설정 `EmulatorKind`).
- 연결 바 컨트롤에 접근성 이름(연결 방식 · 에뮬레이터 종류 · Pi 주소 · Pi 포트) — UI 자동화 검사용.
- 테스트 15개 추가(총 124): 직접 연결 자기 주소 거부 5건, 주소 분류 6건, `WslFakePiTests` 3건(실제 wsl.exe 로 fake_pigpiod.py 기동 → PIGPV/SERVO/GPW 왕복 → Dispose 후 포트 닫힘, 이미 열린 포트 재사용), XAML 바인딩 1건.

## [1.1.1] - 2026-10-27

### Fixed
- 에뮬레이터 모드: 포트 8888 을 다른 프로그램(예: WSL 의 `fake_pigpiod.py` — WSL2 가 그 포트를 Windows 127.0.0.1 로도 열어 줌)이 쓰고 있으면 "연결 실패" 가 나던 문제. 이제 빈 포트로 자동 대체하고 상단·상태 줄에 알림(`EmulatorFallbackPort`).
- 연결 실패 오류 창이 떠 있는 동안 [연결] 버튼이 "연결 중…" 으로 잠겨 보이던 문제 (오류 창 전에 상태를 풂).

### Changed
- Qt 프로그램 관련 항목 제거: "WSL / Pi 관리" 탭의 `deploy.sh` 버튼 삭제, Pi 관리 스크립트(`pi_setup.sh`·`pi_info.sh`)를 `tools\pi` 로 옮기고 기본 스크립트 폴더로 사용. 설계 문서를 WPF 기준으로 정리(0장 요약, 각 장 "쉬운 풀이", 16~18장 작업 일지·명령어·오류 표).

### 계획
- WSL 터널 모드에서 Pi 카메라 스트림(8889)도 ssh `-L` 로 함께 터널링

## [1.1.0] - 2026-10-26

### Added
- 버전 관리: `VERSION` 단일 파일 → 모든 .NET 프로젝트와 Pi 프로그램이 같은 버전을 사용. 창 제목·상태 바에 `v1.1.0` 표시. `tools/bump-version.ps1` / `tools/bump_version.sh` 로 버전 올리기와 CHANGELOG 정리.
- **Pi 화면 프로그램 `pi_display/`** (Python, PyQt5): Pi 에 붙은 모니터(800×480)에 Pi 카메라 영상과 **카메라(서보 팬) 이동 로그**를 표시. pigpiod 를 50 ms 마다 GPW 로 읽어 펄스 변화를 시각·각도·변화량과 함께 기록(파일 `~/.local/share/pi_display/moves.log`). 카메라 스택 자동 강등(picamera2 → libcamera-vid → picamera → OpenCV), `--fake-camera` 로 Pi 없이 시험.
- Pi 프로그램의 **HTTP MJPEG 서버**(포트 8889): `/stream.mjpg`, `/snapshot.jpg`, `/status.json`, `/log.txt`, `/`.
- WPF 카메라 콤보박스에 **"Pi 카메라 (네트워크)"** 항목: `MjpegCameraSource` 가 Pi 의 스트림을 받아 같은 패널에 표시. URL 은 Pi 주소에서 자동 유도되며 직접 입력도 가능.
- WPF "WSL / Pi 관리" 탭에 **[Pi 화면 배포]** 버튼(`pi_display/deploy_pi_display.sh`: rsync → 실행 → 상태 확인), 첫 배포용 **[Pi 화면 배포(+apt) ↗]**.
- `tools/setup_rpi_wireless.sh`: WSL 에서 ssh 키·별명·키 복사까지 한 번에.

### Changed
- 카메라 패널이 노트북 웹캠과 네트워크 카메라를 하나의 목록으로 보여 줌(`CompositeCameraSource`). 웹캠 권한이 없어도 Pi 항목은 보임.
- 설계 문서 10-11 절: 무선 연결 설정을 사용자의 Pi(Bullseye, dhcpcd) 기준으로 보정.

### Notes
- WSL 터널 모드에서는 Pi 주소 칸이 없으므로 카메라 URL 을 직접 지정(`http://<Pi IP>:8889/stream.mjpg`).

## [1.0.0] - 2026-10-26

### Added
- Qt `ServoControl` 과 같은 기능의 WPF 서보 제어 프로그램: 직접 연결 · WSL 터널 · 에뮬레이터 세 가지 연결 방식, 다이얼·각도/펄스 양방향 조작, 위치 메모리·스윕·보정, "WSL / Pi 관리" 탭.
- 왼쪽 카메라 뷰(서보에 올린 카메라의 움직임 그림, 연결 시에만 동작), 노트북 웹캠 패널(WinRT).
- 연결 시 장치의 현재 펄스(GPW)로 화면 맞춤, 해제·종료 시 출력 유지 옵션, 에뮬레이터 상태 파일로 재시작 후에도 위치 기억.
- 설계 과정·운용 원리 PDF, 소스 설명 PDF, xUnit 테스트.
