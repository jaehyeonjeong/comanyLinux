# 변경 내역 (CHANGELOG)

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/) 를 따르고, 버전은 [SemVer](https://semver.org/lang/ko/) 입니다.
버전 번호의 단일 진실은 `VERSION` 파일이며, `tools/bump-version.ps1`(또는 `tools/bump_version.sh`)로 올립니다.

## [Unreleased]

## [1.3.2] - 2026-10-27

### Changed
- **보정 입력칸 연동**: "서보" 탭의 보정 칸에서 각도를 고치면 펄스가, 펄스를 고치면 각도가 따라 바뀌지 않던 문제(사용자 보고). 새 칸 **"1° 당 µs"**(기울기, HS-311 공식 10)로 두 값을 묶음 — `펄스 = 중립 + 각도 × 기울기`. 최소/최대 각도 ↔ 최소/최대 펄스가 서로 따라오고, 중립 펄스나 기울기를 바꾸면 각도를 지킨 채 양쪽 펄스를 다시 계산. 칸 순서를 "1° 당 µs · 중립 · 최소 각도/펄스 · 최대 각도/펄스" 로 바꾸고 아래에 "펄스 = 1500 + 각도 × 10 µs/° → −40° = 1100 µs, +40° = 1900 µs" 요약 줄. Enter 로도 반영. 설정에서 읽어 온 비대칭 보정은 그대로 보여 주고(기울기는 + 쪽에서 계산) 편집할 때부터 연동.
- 테스트 10개(총 218): 각도→펄스, 펄스→각도(0.1° 단위), 중립·기울기 변경 시 각도 유지, 잘못된 기울기 무시, [적용]이 연동된 값으로 모델에 반영, 비대칭 설정 표시, 공식값 버튼이 기울기 복원, XAML 바인딩.

## [1.3.1] - 2026-10-27

### Fixed
- **각도 표시 불일치**: WPF 보정값을 600/1500/2400 µs · ±80° 로 두면 같은 600 µs 를 WPF 는 −80°, 에뮬레이터(앱 안·WSL 가짜 Pi)는 −90°(공식값 10 µs/°)로 보여 주던 문제. pigpiod 는 펄스만 알고 각도는 보는 쪽이 계산하므로, 이제 **WPF 보정값이 기준**이 되어 다른 쪽에 전달된다: 앱 안 가짜 Pi 는 `PigpioEmulatorServer.SetCalibration`, WSL 가짜 Pi 는 띄울 때 `--calib` + 접속 때 pigpio 사용자 명령 **CF1(87)** 두 번(펄스 세 값 / 각도×10 두 값 — `PigpioEmulator.ServoCalibration`), Pi 화면 프로그램(pi_display)은 네트워크 카메라로 붙으면 `POST /servo/calib` 로 자동 전달(`status.json servo.calib` 가 다를 때, "Pi 카메라" 탭에 결과 표시). 보정 탭에서 값을 바꿔도 즉시 다시 전달. 가짜 Pi 와 pi_display 는 받은 보정값을 상태 파일(`PigpioEmulator.json`·`fake_pigpiod.json`·`~/.config/pi_display/servo.json`)에 저장해 다음 실행에도 유지, 기억 중인 각도는 새 보정값으로 다시 계산(펄스는 그대로). 실제 Pi 에서도 pi_display 의 이동 로그 각도가 WPF 와 같아진다.
- 에뮬레이터 상태 파일 형식이 `{"Calib":…, "Gpios":{…}}` 로 바뀜 — 옛 파일(gpio → {Pulse,Angle})도 그대로 읽는다.

### Changed
- `tools/fake_pigpiod.py`: 포트가 이미 쓰이면(`Address already in use`) 스택 추적 대신 누가 듣고 있는지(`ss -ltnp`)와 할 일(그대로 쓰기 / `pkill -f '[f]ake_pigpiod.py'` / 다른 포트)을 한글로 안내하고 종료 코드 1.

## [1.3.0] - 2026-10-27

### Added
- **Pi 카메라 녹화** (OV5647 5 MP 모듈 기준): WPF 카메라 패널의 [● 녹화]/[■ 정지] 버튼으로 Pi 가 영상을 파일로 남김. picamera2 백엔드는 카메라 main 스트림을 **Pi 4 하드웨어 H.264 인코더**로 바로 기록(ffmpeg 있으면 .mp4, 없으면 .h264), 그 밖의 백엔드(libcamera-vid·legacy·USB·가짜 카메라)는 미리보기 MJPEG 를 **순수 Python AVI(MJPG)** 로 저장 (`pidisplay/recorder.py`, `avi_writer.py`). 쓰는 동안 `.part`, 정지 시 최종 이름. 한 번에 최대 60 분, 폴더 8 GB 상한(오래된 것부터 삭제), 여유 500 MB 미만이면 거부. 버튼에 경과 시간·용량 표시, Pi 모니터에도 "● REC".
- **Pi → WSL 자동 전송**: 녹화가 끝나거나 사진을 찍으면 WPF 가 `pi_display/pull_recordings.sh`(rsync over ssh, `*.part` 제외)를 실행해 `~/MyJob/projects/comanyLinux/ServoChapter/recordings` 로 가져옴(설정 `RecordingsDir`·`AutoPullRecordings`). WPF 가 꺼져 있어도 가져오도록 `watch_recordings.sh --install` 이 WSL **systemd user 서비스**(30 초 주기, sudo 불필요)를 등록. 결과는 "Pi 카메라" 탭과 상태 줄에 표시, [폴더 열기] 는 `\\wsl.localhost\…` 를 탐색기로. WSL2 가 NAT 뒤라 Pi→WSL push 는 불가하므로 pull 방식.
- **5 MP 정지 사진** [📷 사진]: picamera2 가 2592×1944 정지 설정으로 한 장 저장(약 1 초 스트림 끊김, 녹화 중이면 녹화 해상도). 같은 폴더에 `img_….jpg` 로 남아 함께 전송.
- **카메라 설정** (오른쪽 새 "Pi 카메라" 탭): 녹화 모드 1080p30 / 720p60 / 480p90, 좌우·상하 반전, 밝기·대비·채도, 화이트밸런스. 즉시 적용되는 값은 `set_controls`, 모드·반전은 카메라를 다시 열어 적용(녹화 중 409 거부). Pi 의 `~/.config/pi_display/camera.json` 에 저장. 슬라이더는 300 ms 모아서 한 번에 전송.
- **인체·사물 인식** (OpenCV): `pidisplay/detector.py` — MobileNet-SSD(VOC 20종, `install_pi_deps.sh` 가 내려받음) 또는 HOG(사람만)로 미리보기 프레임에 박스·이름을 그림(별도 스레드, 3 장마다 추론). 결과는 `status.json` 의 `detect` 와 WPF "인식: 사람 1 · 의자 2 (… fps)". H.264 녹화 원본에는 안 들어감. OpenCV 없는 환경(WSL)은 "인식 불가" 안내.
- pi_display HTTP API: `POST /record/start|stop`, `GET /record/status`, `/recordings.json`, `/recordings/<name>`, `POST /snapshot/save`, `GET/POST /camera/settings`, `/detect/status`, `POST /detect/on|off|config`. 오류는 `{"error":…}` + 4xx/5xx. `status.json` 에 `record`·`detect`·`settings` 블록.
- WPF `Services/PiCameraApi.cs`(API 클라이언트, snake_case JSON), `CameraViewModel` 녹화·사진·설정·인식 멤버와 1 초 상태 폴링, `WslViewModel` [지금 가져오기]/[감시 서비스 설치]/[감시 상태], `MainViewModel.AutoPullRecordings`·[폴더 열기].
- 테스트: xUnit 63개 추가(총 200) — `PiCameraApiTests`(가짜 API 서버), `CameraViewModelRecordingTests`, `RecordingTransferTests`, XAML 바인딩; Python 31개 추가(총 42) — `test_avi_writer`·`test_recorder`·`test_camera_settings`·`test_detector`·`test_mjpeg_server` 확장. 셸 스크립트는 로컬 모드(`--src`, 별명 `local:/경로`)로 검증.

### Changed
- `Picamera2Backend` 가 main(녹화 모드 크기) + lores(미리보기) 두 스트림을 열고 미리보기는 lores 에 MJPEGEncoder(하드웨어) — 구버전이면 JpegEncoder → 단일 스트림으로 강등. 미리보기 publish 는 녹화 fps 에서 간축(60/90 fps 를 네트워크로 다 보내지 않음).
- `install_pi_deps.sh` 가 `ffmpeg python3-opencv` 와 인식 모델(약 23 MB)을 함께 설치(`--no-models` 로 생략), `~/Videos/pi_display` 폴더 생성.
- `MovementLog` 에 `event()` — 녹화 시작/끝·사진·디스크 정리를 서보 로그와 같은 줄에 남김(`kind="rec"`).

### Notes
- 1080p30 H.264·5 MP 사진·인식 fps 는 실제 Pi 카메라가 없어 이 PC 에서 검증하지 못함(코드는 picamera2 공개 API 기준). WSL 가짜 카메라로는 AVI 경로·전송·설정 저장·API 전체를 검증.
- HTTP API 에 인증 없음(LAN 전용). 5 MP 촬영 중 미리보기 약 1 초 정지.

## [1.2.1] - 2026-10-27

### Fixed
- **긴 글이 잘리던 문제**: 상단의 장치 이름·Pi 리비전·가짜 Pi 수신 글, 상태 줄, 카메라 상태, 출력 상태처럼 길이가 들쭉날쭉한 문구를 `ShrinkTextBlock`(새 컨트롤)으로 바꿈. 공간이 모자라면 글꼴을 기준 크기에서 최소 10~11pt 까지 자동으로 줄이고, 그래도 모자라면 "…" 으로 자르면서 전체 글을 툴팁으로 보여 줌. 왼쪽 카메라 그림의 상태 글(위치 유지 중·회전 중·끝단 경고·연결 안 됨)도 폭에 맞춰 글꼴을 줄임.
- 창을 좁히면 상단 연결 바의 [연결]·[종료] 버튼이 오른쪽으로 밀려 보이지 않던 문제: 장치 이름·Pi 리비전·가짜 Pi 수신 글을 연결 바 **아래 전용 줄**로 옮기고(창 폭 전체 사용), 에뮬레이터 종류 설명은 콤보 툴팁으로. 창 최소 폭 1120, ±10° 버튼 여백 축소.

### Added
- 테스트 13개(총 137): `ShrinkTextBlockTests` 8건(넓으면 원래 크기·툴팁 없음 / 좁으면 축소 / 너무 좁으면 최소 크기 + … + 툴팁 / 넓어지면 복원 / 짧은 글로 바뀌면 복원 / 가로 StackPanel 에서는 MaxWidth 가 예산 / 직접 지정한 툴팁 보존 / UI 자동화용 안쪽 TextBlock), XAML 검사 5건(긴 글 자리에 ShrinkTextBlock 사용).

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
