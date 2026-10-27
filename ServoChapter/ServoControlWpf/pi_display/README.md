# pi_display — Pi 모니터용 카메라 영상 + 카메라 이동 로그

Raspberry Pi(모니터 800×480)에서 돌아가는 Python 프로그램입니다.

- 왼쪽: Pi 카메라 영상 (picamera2 → libcamera-vid → picamera → OpenCV 순으로 자동 선택)
- 오른쪽: **카메라(서보 팬) 이동 로그** — pigpiod 에 50 ms 마다 "지금 펄스폭"(GPW)을 물어 **바뀔 때만** 시각·펄스·각도·변화량을 기록. 누가 움직였든(WPF, Qt, 다른 프로그램) 서보가 실제로 받은 값입니다. 파일 `~/.local/share/pi_display/moves.log` 에도 남습니다.
- HTTP 서버(8889): 같은 영상을 WPF 의 "Pi 카메라 (네트워크)" 와 브라우저에 MJPEG 로 보냅니다. `/stream.mjpg` `/snapshot.jpg` `/status.json` `/log.txt` `/`
- **녹화·사진·설정·인식 API** (v1.3.0, WPF 의 [● 녹화]/[📷 사진] 버튼과 "Pi 카메라" 탭이 호출):

  | 메서드·경로 | 하는 일 |
  |---|---|
  | `POST /record/start[?name=]` · `POST /record/stop` · `GET /record/status` | 녹화 시작(이미 녹화 중 409, 디스크 부족 507) / 정지 → `{name,bytes,seconds,frames,kind}` / 상태(경과·용량·디스크) |
  | `GET /recordings.json` · `GET /recordings/<name>` | 녹화·사진 목록(최신 먼저, `*.part` 제외) / 내려받기 |
  | `POST /snapshot/save` | 5 MP(2592×1944) 정지 사진을 녹화 폴더에 `img_….jpg` 로 (picamera2 아니면 미리보기 크기) |
  | `GET /camera/settings` · `POST /camera/settings` | 녹화 모드(1080p30/720p60/480p90)·반전·밝기·대비·채도·화이트밸런스. JSON 본문 또는 쿼리로 바꿀 키만. 모드·반전은 카메라를 다시 열고(녹화 중 409), 값은 `~/.config/pi_display/camera.json` 에 저장 |
  | `GET /detect/status` · `POST /detect/on|off|config?every=3&conf=0.5` | OpenCV 인식(MobileNet-SSD 또는 HOG). 결과는 `status.json` 의 `detect` 에도. OpenCV 없으면 `available:false`(켜기 501) |

  녹화는 picamera2 백엔드면 **Pi 4 하드웨어 H.264**(ffmpeg 있으면 .mp4, 없으면 .h264), 그 밖의 백엔드는 미리보기 MJPEG 를 **순수 Python AVI** 로. 파일은 `~/Videos/pi_display/rec_YYYYmmdd_HHMMSS.*`, 쓰는 동안 `.part`. 한 번에 최대 60 분, 폴더 8 GB 상한(오래된 것부터 삭제).
- **Pi → WSL 전송**: WSL 에서 `./pull_recordings.sh rpi` (rsync, `*.part` 제외, 마지막 줄 `PULLED n`) — WPF 가 녹화 정지·사진 뒤 자동 실행. `./watch_recordings.sh rpi --install` 은 30 초마다 가져오는 systemd user 서비스(sudo 불필요). 기본 목적지 `~/MyJob/projects/comanyLinux/ServoChapter/recordings`. Pi 없이 시험: 별명 자리에 `local:/경로`.
- (선택) UDP 8890 으로 WPF 가 보내는 메모("WPF +20.0° 1700us")를 로그 줄에 붙입니다.

## Pi 에 배포 (WSL 에서)

```bash
./deploy_pi_display.sh rpi --install-deps --autostart   # 처음 한 번: apt 설치 + 로그인 시 자동 시작
./deploy_pi_display.sh rpi                              # 그 뒤: 복사 → 재실행 → status.json 확인
./deploy_pi_display.sh rpi -- --fps 15 --quality 60     # 추가 플래그
```
WPF 의 "WSL / Pi 관리" 탭 [Pi 화면 배포] / [Pi 화면 배포(+apt) ↗] 버튼이 같은 스크립트를 실행합니다.

```bash
ssh rpi "pkill -f '[p]i_display.py'"                    # 끄기 — [p] 필수 (없으면 자기 ssh 셸이 먼저 죽음)
ssh rpi "pgrep -af '[p]i_display.py' || echo 꺼짐"      # 확인
ssh rpi 'tail -f ~/Source/pi_display/pi_display.log'   # 로그
ssh rpi 'rm ~/.config/autostart/pi-display.desktop'    # --autostart 로 등록한 자동 시작 해제
```
Pi 모니터 앞에서는 ESC 또는 Q 로도 종료됩니다.
ssh 별명 `rpi` 는 `../tools/setup_rpi_wireless.sh` 로 준비합니다.

## Pi 없이 시험 (WSL)

```bash
# 터미널 1: 가짜 pigpiod            터미널 2: 가짜 카메라 + 서버 (화면 없음)
cd ~/MyJob/projects/comanyLinux/ServoChapter/ServoControlWpf
./run-from-wsl.sh fakepi             python3 pi_display/pi_display.py --no-gui --fake-camera --pigpio-host 127.0.0.1
```
Windows 의 WPF 에서 에뮬레이터 · WSL 가짜 Pi · 8888 → 카메라 콤보 "Pi 카메라 (네트워크)" (URL `http://127.0.0.1:8889/stream.mjpg`) → 다이얼을 돌리면 터미널 2에 `로그: 17:05:01.123  1700 µs  +20.0°  Δ+10.0°` 가 찍힙니다. [● 녹화] → [■ 정지] 하면 `● 녹화 시작 rec_….avi (mjpeg-avi)` / `■ 녹화 끝 … 56 KB 00:00:06` 이 찍히고, ssh 별명 칸에 `local:/home/<나>/Videos/pi_display` 를 두면 전송 스크립트까지 Pi 없이 돌아갑니다.
단위 테스트(47개): `python3 -m unittest discover -s pi_display/tests -v`

## 주요 플래그

| 플래그 | 기본 | 뜻 |
|---|---|---|
| `--camera auto\|picamera2\|libcamera-vid\|picamera\|opencv\|fake\|none`, `--fake-camera` | auto | 카메라 백엔드. `auto` 가 넷 다 실패하면(카메라 미연결) 종료하지 않고 `none`(자리 표시 "NO CAMERA" 1 fps)으로 계속 돈다 — 서보 로그·status.json·보정 수신은 그대로, 녹화·사진은 503. 카메라를 붙인 뒤 다시 시작 |
| `--width/--height/--fps/--quality` | 640/480/10/70 | 영상 크기·속도·JPEG 품질 (Wi-Fi 대역폭 조절) |
| `--http-port/--http-bind`, `--no-http` | 8889 / 0.0.0.0 | MJPEG 서버 |
| `--fullscreen` / `--windowed` / `--no-gui` | 전체 화면 | 화면 모드 (PyQt5 가 없으면 자동으로 `--no-gui`) |
| `--pigpio-host/--pigpio-port/--gpio/--poll-ms` | 127.0.0.1/8888/18/50 | 서보 상태 읽기 |
| `--calib 1100,1500,1900,-40,40` | HS-311 공식값 | **WPF 의 보정값과 같게** 두어야 각도가 같게 찍힘 |
| `--log-file PATH`, `--no-log-file` | `~/.local/share/pi_display/moves.log` | 로그 파일 |
| `--note-port`, `--no-notes` | 8890 | WPF 메모 수신 |
| `--rec-dir`, `--rec-max-minutes`, `--rec-max-gb`, `--rec-bitrate`, `--no-record` | `~/Videos/pi_display` / 60 / 8 / 모드별 | 녹화 폴더·상한·비트레이트 |
| `--record-mode 1080p30\|720p60\|480p90`, `--camera-settings PATH` | camera.json 값 / `~/.config/pi_display/camera.json` | 시작 시 녹화 모드(지정 시 저장값보다 우선), 설정 파일 |
| `--detect`, `--detect-every`, `--detect-conf`, `--model-dir`, `--no-detect` | 꺼짐 / 3 / 0.5 / `~/.local/share/pi_display/models` | OpenCV 인식 |
| `--version` | | 버전 (`../VERSION` 또는 `../../VERSION`) |

키: ESC/Q 종료, F11 전체 화면 전환. 녹화 중에는 상단에 "● REC 00:01:23 · 12 MB".

## Bullseye 패키지 (install_pi_deps.sh 가 설치)
`python3-pyqt5 python3-pigpio python3-pil rsync curl ffmpeg python3-opencv python3-numpy` + libcamera 스택이면 `python3-picamera2 libcamera-apps`, 레거시 스택이면 `python3-picamera`, 선택 `scrot`. 인식 모델(MobileNet-SSD, 약 23 MB)은 GitHub 에서 내려받으며 `--no-models` 로 생략(그러면 HOG 로 사람만).
