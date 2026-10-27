# pi_display — Pi 모니터용 카메라 영상 + 카메라 이동 로그

Raspberry Pi(모니터 800×480)에서 돌아가는 Python 프로그램입니다.

- 왼쪽: Pi 카메라 영상 (picamera2 → libcamera-vid → picamera → OpenCV 순으로 자동 선택)
- 오른쪽: **카메라(서보 팬) 이동 로그** — pigpiod 에 50 ms 마다 "지금 펄스폭"(GPW)을 물어 **바뀔 때만** 시각·펄스·각도·변화량을 기록. 누가 움직였든(WPF, Qt, 다른 프로그램) 서보가 실제로 받은 값입니다. 파일 `~/.local/share/pi_display/moves.log` 에도 남습니다.
- HTTP 서버(8889): 같은 영상을 WPF 의 "Pi 카메라 (네트워크)" 와 브라우저에 MJPEG 로 보냅니다. `/stream.mjpg` `/snapshot.jpg` `/status.json` `/log.txt` `/`
- (선택) UDP 8890 으로 WPF 가 보내는 메모("WPF +20.0° 1700us")를 로그 줄에 붙입니다.

## Pi 에 배포 (WSL 에서)

```bash
./deploy_pi_display.sh rpi --install-deps --autostart   # 처음 한 번: apt 설치 + 로그인 시 자동 시작
./deploy_pi_display.sh rpi                              # 그 뒤: 복사 → 재실행 → status.json 확인
./deploy_pi_display.sh rpi -- --fps 15 --quality 60     # 추가 플래그
```
WPF 의 "WSL / Pi 관리" 탭 [Pi 화면 배포] / [Pi 화면 배포(+apt) ↗] 버튼이 같은 스크립트를 실행합니다.
ssh 별명 `rpi` 는 `../tools/setup_rpi_wireless.sh` 로 준비합니다.

## Pi 없이 시험 (WSL)

```bash
# 터미널 1: 가짜 pigpiod            터미널 2: 가짜 카메라 + 서버 (화면 없음)
cd ~/MyJob/ServoChapter/ServoControlWpf
./run-from-wsl.sh fakepi             python3 pi_display/pi_display.py --no-gui --fake-camera --pigpio-host 127.0.0.1
```
Windows 의 WPF 에서 직접 연결 `127.0.0.1:8888` → 카메라 콤보 "Pi 카메라 (네트워크)" (URL `http://127.0.0.1:8889/stream.mjpg`) → 다이얼을 돌리면 터미널 2에 `로그: 17:05:01.123  1700 µs  +20.0°  Δ+10.0°` 가 찍힙니다.
단위 테스트: `python3 -m unittest discover -s pi_display/tests -v`

## 주요 플래그

| 플래그 | 기본 | 뜻 |
|---|---|---|
| `--camera auto\|picamera2\|libcamera-vid\|picamera\|opencv\|fake`, `--fake-camera` | auto | 카메라 백엔드 |
| `--width/--height/--fps/--quality` | 640/480/10/70 | 영상 크기·속도·JPEG 품질 (Wi-Fi 대역폭 조절) |
| `--http-port/--http-bind`, `--no-http` | 8889 / 0.0.0.0 | MJPEG 서버 |
| `--fullscreen` / `--windowed` / `--no-gui` | 전체 화면 | 화면 모드 (PyQt5 가 없으면 자동으로 `--no-gui`) |
| `--pigpio-host/--pigpio-port/--gpio/--poll-ms` | 127.0.0.1/8888/18/50 | 서보 상태 읽기 |
| `--calib 1100,1500,1900,-40,40` | HS-311 공식값 | **WPF 의 보정값과 같게** 두어야 각도가 같게 찍힘 |
| `--log-file PATH`, `--no-log-file` | `~/.local/share/pi_display/moves.log` | 로그 파일 |
| `--note-port`, `--no-notes` | 8890 | WPF 메모 수신 |
| `--version` | | 버전 (`../VERSION` 또는 `../../VERSION`) |

키: ESC/Q 종료, F11 전체 화면 전환.

## Bullseye 패키지 (install_pi_deps.sh 가 설치)
`python3-pyqt5 python3-pigpio python3-pil rsync curl` + libcamera 스택이면 `python3-picamera2 libcamera-apps`, 레거시 스택이면 `python3-picamera`, 선택 `python3-opencv scrot`.
