# ServoControlWpf — Windows(WPF)에서 WSL·Raspberry Pi 서보 제어

**버전 1.2.1** — 버전은 `VERSION` 파일 하나가 기준이고(창 제목·상태 바에 표시), 변경 내역은 [CHANGELOG.md](CHANGELOG.md), 올릴 때는 `tools\bump-version.ps1 -Part patch|minor|major`(WSL: `tools/bump_version.sh`).

Windows(WPF, C#)에서 Raspberry Pi 의 서보 모터(카메라 팬)를 움직이는 프로그램입니다. v1.1.0 부터는 Pi 모니터에 띄우는 **카메라 영상 + 카메라 이동 로그** 프로그램(`pi_display/`, Python)이 함께 들어 있고, WPF 에서 그 영상을 받아 봅니다.
HiTEC HS-311 서보를 Raspberry Pi 4B 의 pigpiod(TCP 8888)로 제어하며, 세 가지 연결 방식을 지원합니다.

| 연결 방식 | 경로 | 언제 쓰나 |
|---|---|---|
| **직접 연결** | Windows → LAN → **실제 Pi** `192.168.10.2:8888` (또는 Wi-Fi `172.30.1.81`) | 평소. Pi 쪽에 `pi_setup.sh` 로 원격 허용을 해 둔 경우. `127.0.0.1`·`localhost` 같은 자기 주소는 거부하고 에뮬레이터로 안내 |
| **WSL 터널** | Windows → `wsl.exe` → `ssh -L` → Pi `127.0.0.1:8888` | 모든 통신을 WSL/SSH 로 보내고 싶을 때. pigpiod 원격 허용 설정이 없어도 됨 |
| **에뮬레이터** | **앱 안 가짜 Pi**: Windows → `127.0.0.1` → 앱 안의 가짜 pigpiod / **WSL 가짜 Pi**: Windows → `127.0.0.1` → (WSL2 localhost 포워딩) → WSL 의 `tools/fake_pigpiod.py` | Pi 가 없을 때. 실제와 같은 TCP 명령 경로를 그대로 검증. WSL 가짜 Pi 는 명령이 가상 네트워크를 실제로 건너감 |

"WSL / Pi 관리" 탭에서는 Pi 관리 스크립트(`tools/pi/pi_setup.sh`, `pi_info.sh`), Pi 화면 프로그램 배포(`pi_display/deploy_pi_display.sh`), `ssh rpi …` 명령을 WPF 안에서 실행합니다.

## 폴더

```
ServoControlWpf.sln
ServoControlWpf/            WPF 앱 (net8.0-windows10.0.19041.0)   Model / ViewModel / View / Services(WSL, 웹캠)
ServoControlWpf.Tests/      xUnit 테스트 137개 (사양 변환, 글꼴 자동 축소, pigpiod 프로토콜, 모델 롤백·장치 상태 동기화, 에뮬레이터 왕복·재연결 기억·상태 파일·포트 충돌 대체, WSL 가짜 Pi 기동·재사용, 직접 연결 자기 주소 거부, 다이얼·카메라 뷰, 각도↔펄스, 터널 실패 경로, XAML 바인딩 누락 검사, 카메라 뷰모델, MJPEG 파서·네트워크 카메라·합성 소스, 버전 일치)
tools/fake_pigpiod.py       리눅스(WSL)용 가짜 pigpiod — 에뮬레이터 종류 "WSL 가짜 Pi" 가 wsl.exe 로 띄움
tools/pi/                   Pi 관리 스크립트 pi_setup.sh(pigpio 설치·원격 허용) · pi_info.sh(Pi 정보 수집)
pi_display/                 Pi 모니터용 Python 프로그램: 카메라 영상 + 서보 이동 로그 + MJPEG 서버 (pi_display/README.md)
VERSION · CHANGELOG.md      버전 단일 진실 · 변경 내역.  Directory.Build.props 가 VERSION 을 모든 프로젝트에 주입
PigpioEmulator/             가짜 pigpiod 라이브러리 (16바이트 프로토콜 + 서보 움직임 흉내)
PigpioEmulatorCli/          가짜 pigpiod 콘솔 프로그램 `pigpio-emu`
```

## 만들기·실행

```powershell
cd ServoControlWpf
dotnet build ServoControlWpf.sln -c Release
dotnet test  ServoControlWpf.sln -c Release
dotnet run   --project ServoControlWpf
```

.NET SDK 8 이상이 필요합니다 (SDK 10 으로도 빌드됩니다, 런타임은 8 또는 그 이상 어느 것이든 됨). 앱의 대상 프레임워크는 `net8.0-windows10.0.19041.0`(웹캠용 Windows 10 API 사용)이라 출력 폴더가 `bin\Release\net8.0-windows10.0.19041.0\` 입니다. 처음 빌드할 때 Windows SDK 프로젝션 패키지를 자동으로 내려받습니다(인터넷 필요).

## 하드웨어 없이 써 보기 (에뮬레이터)

연결 방식 **에뮬레이터** 를 고르면 옆에 **종류** 콤보가 나타납니다.

1. **앱 안 가짜 Pi** (기본) → [연결]. 상단에 `Pi 리비전 c03111 · pigpio v79` 와 "가짜 Pi 수신 #n: SERVO gpio=18 1500 µs" 가 표시되면 TCP 로 명령이 실제로 오간 것입니다. 포트 8888 을 다른 프로그램이 이미 쓰고 있으면 앱이 빈 포트로 자동 대체하고 상단에 "임시 포트 NNNN" 이라고 알립니다.
2. **WSL 가짜 Pi (fake_pigpiod.py)** → [연결]. 앱이 `wsl.exe --cd tools -e python3 fake_pigpiod.py --port 8888 --bind 0.0.0.0` 으로 **리눅스 프로세스**를 띄우고 `127.0.0.1:8888` 로 접속합니다(WSL2 가 WSL 안의 포트를 Windows 의 127.0.0.1 로도 열어 줌). 가짜 Pi 가 받은 명령(`✓ SERVO gpio=18 1700 µs → 목표 +20.0°`)이 상단에 그대로 나옵니다. WSL 터미널에서 `./run-from-wsl.sh fakepi` 로 먼저 띄워 두었다면 그것을 재사용하고(로그는 그 터미널에), 앱이 띄운 것은 앱 종료 시 함께 끝납니다. 위치 기억은 `~/.config/VI/fake_pigpiod.json` 에 남습니다.
3. 콘솔 에뮬레이터도 있습니다(별도 창에서 명령 로그와 위치 막대를 보고 싶을 때):
   ```powershell
   dotnet run --project PigpioEmulatorCli            # 127.0.0.1:8888 → WPF: 에뮬레이터 · WSL 가짜 Pi (포트가 이미 열려 있어 그대로 접속)
   dotnet run --project PigpioEmulatorCli -- 8888 0.0.0.0   # WSL 쪽 프로그램(pi_display 등)이 Windows 호스트 IP 로 접속 가능
   ```

**직접 연결은 실제 Pi 전용**입니다. Pi 주소에 `127.0.0.1`·`localhost` 를 넣고 [연결] 하면 "직접 연결은 실제 Pi(LAN 주소)에만 씁니다 … 에뮬레이터로" 라는 안내와 함께 거부됩니다(v1.2.0). 그 전까지 "직접 연결 127.0.0.1 + WSL fakepi" 로 하던 연습은 2번이 대신합니다.

## WSL 셸에서 띄우기

프로그램 폴더를 WSL 쪽(예: `~/MyJob/projects/comanyLinux/ServoChapter/ServoControlWpf`)에 두었다면 WSL 터미널에서 바로 띄울 수 있습니다. WSL interop이 Windows 프로그램을 대신 실행하므로 창은 Windows 화면에 뜨고, 연결 방식은 Windows에서 띄웠을 때와 같습니다.

```bash
cd ~/MyJob/projects/comanyLinux/ServoChapter/ServoControlWpf
chmod +x run-from-wsl.sh
./run-from-wsl.sh          # 실행 (Release 없으면 Debug). 복사한 .exe 의 실행 비트도 자동으로 켬
./run-from-wsl.sh build    # Windows 의 dotnet.exe 로 빌드 후 실행
./run-from-wsl.sh fakepi   # WSL 자체를 가짜 Pi 로 (tools/fake_pigpiod.py, 0.0.0.0:8888) → WPF 에서 에뮬레이터 · WSL 가짜 Pi (그대로 재사용)
```

다른 방법: `dotnet.exe "$(wslpath -w …/ServoControlWpf.dll)"` 도 됩니다. `cmd.exe /c start` 는 UNC 경로를 지원하지 않습니다.

### 리눅스 dotnet(WSL 안에 apt/snap 으로 설치한 .NET)으로 할 수 있는 것과 없는 것

| 명령 (WSL 셸) | 결과 |
|---|---|
| `dotnet run --project ServoControlWpf` | ✗ WPF 는 Windows 전용. 원래는 `NETSDK1100` 오류가 나지만, `Directory.Build.targets` 가 가로채 한글 안내("./run-from-wsl.sh build 를 쓰세요")를 냅니다 |
| `dotnet build ServoControlWpf.Tests` | ✗ 같은 이유 (WPF 참조) |
| `dotnet run --project PigpioEmulatorCli -- 8888 0.0.0.0` | ✔ 가짜 pigpiod 가 **리눅스 프로세스**로 돌아감 → WPF 에서 에뮬레이터 · WSL 가짜 Pi(포트가 이미 열려 있으면 그대로 접속). `fakepi`(python) 와 같은 역할 |
| `dotnet build PigpioEmulator` | ✔ |

`tools/portcheck` 는 "Pi 로 옮길 수 있나" 확인용 프로젝트입니다. Model·Services 층을 WPF 없이 컴파일해 보는 것으로, WSL 에서 `dotnet build tools/portcheck` 가 통과하면 화면(View)만 Avalonia 로 바꿔 Pi 에서 돌릴 수 있다는 뜻입니다(설계 PDF 14-1).

즉 리눅스 dotnet 은 **없어도 됩니다**. WPF 실행은 항상 Windows 의 `dotnet.exe`/`.exe` 가 맡고, `run-from-wsl.sh` 가 그 일을 대신해 줍니다. (Ubuntu 26.04 의 apt 패키지 이름은 `dotnet-sdk-10.0` 입니다. snap 판은 `libunwind8` 이 없으면 실행이 안 됩니다.)

WSL 에 sshd 를 설치하고 `~/.ssh/config` 에 `Host rpi → HostName 127.0.0.1` 을 두면 "WSL 터널" 모드도 WSL 가짜 Pi 로 시험할 수 있습니다(설계 PDF 13-2).

## 무선(Wi-Fi) 으로 연결하기

Pi 를 Wi-Fi 에 붙이고(`sudo nmcli dev wifi connect "SSID" password "…"`, IP 고정 권장) WSL 에서 한 번:

```bash
./tools/setup_rpi_wireless.sh 192.168.0.50 pi   # ssh 키 생성 → ~/.ssh/config 의 Host rpi → Pi 에 키 복사 → 확인
```

그 다음 WPF 에서 **WSL 터널** 모드(ssh 별명 `rpi`)로 [연결]하면 Pi 쪽 추가 설정 없이 동작합니다(PC IP 가 바뀌어도 됨). **직접 연결**을 쓰려면 Pi 의 pigpiod 가 PC 의 Wi-Fi IP 를 허용해야 하므로 `./tools/pi/pi_setup.sh rpi <PC_WIFI_IP>` 를 실행합니다(스크립트가 그 IP 를 알려 줌). 자세한 순서와 점검표는 설계 PDF 10-11.

## 실제 Pi 로 쓰기

1. (처음 한 번) WSL 에서 `./tools/pi/pi_setup.sh rpi <PC IP>` — pigpio 설치 + PC 주소 원격 허용. WPF 의 "WSL / Pi 관리" 탭 → [pi_setup.sh ↗] 로도 실행됩니다 (새 터미널 창).
2. 직접 연결: Pi 주소 `192.168.10.2`, 포트 `8888` → [연결] → [0°] 로 중앙 확인.
3. WSL 터널: ssh 별명 `rpi` (WSL 의 `~/.ssh/config`, 키 인증) → [연결]. 앱이 `wsl.exe -e ssh -N -L 127.0.0.1:18888:127.0.0.1:8888 rpi` 를 띄우고 `127.0.0.1:18888` 로 접속합니다.
4. 창을 닫으면 (6번 옵션이 꺼져 있을 때) 펄스 0 을 보내 서보 힘을 뺍니다. 터널·앱 안 에뮬레이터·앱이 띄운 WSL 가짜 Pi 도 함께 종료됩니다.
5. **연결할 때 화면을 실제 서보 상태에 맞춥니다.** pigpiod 에 "지금 이 핀의 서보 펄스폭" (GPW 명령)을 물어, 서보가 이미 1700 µs 로 구동 중이면 다이얼·슬라이더·카메라가 +20° 로 맞춰집니다. 다른 프로그램이나 이전 세션이 남긴 상태도 그대로 반영됩니다.
6. **연결 해제·종료 시 서보 출력 유지** (서보 탭의 체크박스, 기본 켜짐): [연결 해제]나 창 닫기 때 펄스 0 을 보내지 않아 서보가 자리를 지키고, 다음에 [연결]하면(프로그램을 다시 켠 뒤라도) 5번처럼 그 위치로 화면이 맞춰집니다. 끄면 힘을 빼고, 재연결 시 "출력 꺼짐 + 마지막으로 보낸 각도" 로 표시합니다.
7. **프로그램을 껐다 켜도 이어집니다.** 마지막으로 서보에 보낸 각도를 설정 파일에 저장해 다음 실행은 그 각도에서 시작하고(연결 전에도), 연결하면 장치 보고(GPW)로 확정합니다. 에뮬레이터도 `%APPDATA%\VI\PigpioEmulator.json` 에 펄스·각도를 저장해 "같은 가짜 서보" 로 다시 뜨므로, 실제 Pi 를 붙였을 때 일어날 일을 Pi 없이 그대로 연습할 수 있습니다. 콘솔 `pigpio-emu` 와 리눅스 `fake_pigpiod.py`(`~/.config/VI/fake_pigpiod.json`, `--state ""` 로 끔)도 같은 방식입니다.

**왼쪽 위 "카메라 화면"** 은 실제 카메라 영상입니다. 프로그램을 켜면 카메라 장치 목록을 읽어 콤보박스에 넣고, 저장된 장치(없으면 첫 번째 = 보통 노트북 내장 카메라)를 자동으로 켭니다. [정지]/[시작], [새로 고침](장치 목록 다시 읽기), "꺼짐" 선택이 됩니다. Windows 내장 API(WinRT MediaCapture)로 받으므로 외부 패키지가 없고, 데스크톱 앱의 카메라 권한(Windows 설정 > 개인 정보 및 보안 > 카메라)이 켜져 있어야 합니다.
같은 목록의 **"Pi 카메라 (네트워크)"** 를 고르면 Pi 에서 도는 `pi_display` 의 MJPEG 스트림(`http://<Pi 주소>:8889/stream.mjpg`, URL 칸에서 바꿀 수 있음)을 같은 자리에 보여 줍니다. 웹캠 권한이 없어도 이 항목은 보입니다.

## Pi 화면 프로그램 (pi_display) — 카메라 영상 + 카메라 이동 로그

Pi 모니터(800×480)에 Pi 카메라 영상과 **서보가 실제로 움직인 기록**(pigpiod 를 50 ms 마다 GPW 로 읽어 바뀔 때만 시각·펄스·각도·변화량)을 띄우고, 영상을 HTTP 8889 로 내보냅니다. 자세한 플래그·배포는 `pi_display/README.md`.

```bash
./pi_display/deploy_pi_display.sh rpi --install-deps --autostart   # 처음 한 번 (WPF: [Pi 화면 배포(+apt) ↗])
./pi_display/deploy_pi_display.sh rpi                              # 그 뒤 (WPF: [Pi 화면 배포])
```

Pi 없이 전체 흐름: WSL 1 `./run-from-wsl.sh fakepi`, WSL 2 `python3 pi_display/pi_display.py --no-gui --fake-camera --pigpio-host 127.0.0.1`, WPF **에뮬레이터 · WSL 가짜 Pi**(8888, 이미 떠 있는 WSL 1 을 재사용) + 카메라 콤보 "Pi 카메라 (네트워크)". 다이얼을 돌리면 WSL 2 에 `로그: … 1700 µs +20.0° Δ+10.0°` 가 찍힙니다.

**왼쪽 아래**는 서보에 올려진 카메라를 위에서 본 그림입니다(표시 전용, 마우스 입력 없음). 명령한 각도를 향해 HS-311 속도(0.19 s/60°)로 돌아가고, 시야(62°)·허용 범위·끝단 경고를 보여 줍니다.
**연결된 상태에서만 움직입니다.** 연결 해제 상태에서는 "연결 안 됨" 안내가 덮이고, 다이얼·각도·펄스를 바꿔도 카메라는 제자리입니다(명령이 서보로 나가지 않으므로).
다이얼: 노브를 끌어 돌리기(1° 단위), 휠 1칸 = 1°, 다이얼에 포커스가 있으면 ←/→ 1°, PgUp/PgDn 10°, Home/End 끝.
각도 줄과 펄스 줄(슬라이더 + 입력칸)은 서로 묶여 있습니다. 펄스폭을 1650 µs 로 바꾸면 각도가 15.0° 로, 각도를 −20° 로 바꾸면 펄스폭이 1300 µs 로 바뀌고 그 값이 서보로 나갑니다. 범위 밖 펄스는 보정값의 최소~최대로 잘립니다.
키보드: ←/→ 1°, Space 출력 끄기, F11 전체화면.  설정 저장 위치: `%APPDATA%\VI\ServoControl.json` (마지막 각도 포함), 에뮬레이터 상태: `%APPDATA%\VI\PigpioEmulator.json` (지우면 가짜 서보가 0°·출력 꺼짐으로 초기화).

설계 과정·운용 원리 문서: `..\ServoControlWpf_설계문서\ServoControlWpf_설계과정.pdf`
소스 코드 설명(클래스별 역할·협력 관계·어디를 고치면 되나): `..\ServoControlWpf_설계문서\ServoControlWpf_소스설명.pdf`
작업 일지·리눅스/WSL/Pi 명령어 치트시트·오류와 해결은 설계과정 PDF 의 0장(한 장 요약)과 16~18장에 있습니다.
