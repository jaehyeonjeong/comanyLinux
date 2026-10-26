# ServoControlWpf — Windows(WPF)에서 WSL·Raspberry Pi 서보 제어

기존 Qt/C++ `ServoControl`(WSL에서 실행)과 **같은 일을 하는 WPF(C#) 프로그램**입니다.
HiTEC HS-311 서보를 Raspberry Pi 4B 의 pigpiod(TCP 8888)로 제어하며, 세 가지 연결 방식을 지원합니다.

| 연결 방식 | 경로 | 언제 쓰나 |
|---|---|---|
| **직접 연결** | Windows → 이더넷 → Pi `192.168.10.2:8888` | 평소. Pi 쪽에 `pi_setup.sh` 로 원격 허용을 해 둔 경우 |
| **WSL 터널** | Windows → `wsl.exe` → `ssh -L` → Pi `127.0.0.1:8888` | 모든 통신을 WSL/SSH 로 보내고 싶을 때. pigpiod 원격 허용 설정이 없어도 됨 |
| **에뮬레이터** | Windows → `127.0.0.1` → 앱 안의 가짜 pigpiod | Pi 가 없을 때. 실제와 같은 TCP 명령 경로를 그대로 검증 |

"WSL / Pi 관리" 탭에서는 기존 셸 스크립트(`pi_setup.sh`, `deploy.sh`, `pi_info.sh`)와 `ssh rpi …` 명령을 WPF 안에서 실행합니다.

## 폴더

```
ServoControlWpf.sln
ServoControlWpf/            WPF 앱 (net8.0-windows)   Model / ViewModel / View / Services
ServoControlWpf.Tests/      xUnit 테스트 53개 (사양 변환, pigpiod 프로토콜, 모델 롤백, 에뮬레이터 왕복, 다이얼·카메라 뷰, 각도↔펄스, 터널 실패 경로)
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

.NET SDK 8 이상이 필요합니다 (SDK 10 으로도 빌드됩니다, 런타임은 8 또는 그 이상 어느 것이든 됨).

## 하드웨어 없이 써 보기 (에뮬레이터)

1. 연결 방식 **에뮬레이터** → [연결]. 상단에 `Pi 리비전 c03111 · pigpio v79` 와 "가짜 Pi 수신 #n: SERVO gpio=18 1500 µs" 가 표시되면 TCP 로 명령이 실제로 오간 것입니다.
2. 콘솔 에뮬레이터로도 할 수 있습니다:
   ```powershell
   dotnet run --project PigpioEmulatorCli            # 127.0.0.1:8888
   dotnet run --project PigpioEmulatorCli -- 8888 0.0.0.0   # WSL 의 Qt ServoControl 에서 Windows 호스트 IP 로 접속 가능
   ```
   그 다음 WPF 에서 **직접 연결**, Pi 주소 `127.0.0.1` 로 [연결].

## WSL 셸에서 띄우기

프로그램 폴더를 WSL 쪽(예: `~/MyJob/ServoChapter/ServoControlWpf`)에 두었다면 WSL 터미널에서 바로 띄울 수 있습니다. WSL interop이 Windows 프로그램을 대신 실행하므로 창은 Windows 화면에 뜨고, 연결 방식은 Windows에서 띄웠을 때와 같습니다.

```bash
cd ~/MyJob/ServoChapter/ServoControlWpf
chmod +x run-from-wsl.sh
./run-from-wsl.sh          # 실행 (Release 없으면 Debug). 복사한 .exe 의 실행 비트도 자동으로 켬
./run-from-wsl.sh build    # Windows 의 dotnet.exe 로 빌드 후 실행
./run-from-wsl.sh fakepi   # WSL 자체를 가짜 Pi 로 (tools/fake_pigpiod.py, 0.0.0.0:8888) → WPF 에서 직접 연결 127.0.0.1:8888
```

다른 방법: `dotnet.exe "$(wslpath -w …/ServoControlWpf.dll)"` 도 됩니다. `cmd.exe /c start` 는 UNC 경로를 지원하지 않습니다.

### 리눅스 dotnet(WSL 안에 apt/snap 으로 설치한 .NET)으로 할 수 있는 것과 없는 것

| 명령 (WSL 셸) | 결과 |
|---|---|
| `dotnet run --project ServoControlWpf` | ✗ WPF 는 Windows 전용. 원래는 `NETSDK1100` 오류가 나지만, `Directory.Build.targets` 가 가로채 한글 안내("./run-from-wsl.sh build 를 쓰세요")를 냅니다 |
| `dotnet build ServoControlWpf.Tests` | ✗ 같은 이유 (WPF 참조) |
| `dotnet run --project PigpioEmulatorCli -- 8888 0.0.0.0` | ✔ 가짜 pigpiod 가 **리눅스 프로세스**로 돌아감 → WPF 에서 직접 연결 `127.0.0.1:8888`. `fakepi`(python) 와 같은 역할 |
| `dotnet build PigpioEmulator` | ✔ |

`tools/portcheck` 는 "Pi 로 옮길 수 있나" 확인용 프로젝트입니다. Model·Services 층을 WPF 없이 컴파일해 보는 것으로, WSL 에서 `dotnet build tools/portcheck` 가 통과하면 화면(View)만 Avalonia 로 바꿔 Pi 에서 돌릴 수 있다는 뜻입니다(설계 PDF 14-1).

즉 리눅스 dotnet 은 **없어도 됩니다**. WPF 실행은 항상 Windows 의 `dotnet.exe`/`.exe` 가 맡고, `run-from-wsl.sh` 가 그 일을 대신해 줍니다. (Ubuntu 26.04 의 apt 패키지 이름은 `dotnet-sdk-10.0` 입니다. snap 판은 `libunwind8` 이 없으면 실행이 안 됩니다.)

Pi 없이 전체 흐름 연습: 터미널 1에서 `./run-from-wsl.sh fakepi`, 터미널 2에서 `./run-from-wsl.sh`, WPF 에서 직접 연결 `127.0.0.1:8888` → 터미널 1에 HWVER·PIGPV·SERVO 명령이 찍힙니다. WSL 에 sshd 를 설치하고 `~/.ssh/config` 에 `Host rpi → HostName 127.0.0.1` 을 두면 "WSL 터널" 모드도 같은 가짜 Pi 로 시험할 수 있습니다(설계 PDF 13-2).

## 실제 Pi 로 쓰기

1. (처음 한 번) WSL 에서 `./pi_setup.sh` — pigpio 설치 + PC(192.168.10.1) 원격 허용. WPF 의 "WSL / Pi 관리" 탭 → [pi_setup.sh ↗] 로도 실행됩니다 (새 터미널 창).
2. 직접 연결: Pi 주소 `192.168.10.2`, 포트 `8888` → [연결] → [0°] 로 중앙 확인.
3. WSL 터널: ssh 별명 `rpi` (WSL 의 `~/.ssh/config`, 키 인증) → [연결]. 앱이 `wsl.exe -e ssh -N -L 127.0.0.1:18888:127.0.0.1:8888 rpi` 를 띄우고 `127.0.0.1:18888` 로 접속합니다.
4. 창을 닫으면 항상 펄스 0 을 보내 서보 힘을 뺍니다 (터널/에뮬레이터도 함께 종료).

왼쪽 화면은 서보에 올려진 카메라를 위에서 본 그림입니다(표시 전용, 마우스 입력 없음). 명령한 각도를 향해 HS-311 속도(0.19 s/60°)로 돌아가고, 시야(62°)·허용 범위·끝단 경고를 보여 줍니다.
**연결된 상태에서만 움직입니다.** 연결 해제 상태에서는 "연결 안 됨" 안내가 덮이고, 다이얼·각도·펄스를 바꿔도 카메라는 제자리입니다(명령이 서보로 나가지 않으므로).
다이얼: 노브를 끌어 돌리기(1° 단위), 휠 1칸 = 1°, 다이얼에 포커스가 있으면 ←/→ 1°, PgUp/PgDn 10°, Home/End 끝.
각도 줄과 펄스 줄(슬라이더 + 입력칸)은 서로 묶여 있습니다. 펄스폭을 1650 µs 로 바꾸면 각도가 15.0° 로, 각도를 −20° 로 바꾸면 펄스폭이 1300 µs 로 바뀌고 그 값이 서보로 나갑니다. 범위 밖 펄스는 보정값의 최소~최대로 잘립니다.
키보드: ←/→ 1°, Space 출력 끄기, F11 전체화면.  설정 저장 위치: `%APPDATA%\VI\ServoControl.json`.

설계 과정·운용 원리 문서: `..\ServoControlWpf_설계문서\ServoControlWpf_설계과정.pdf`
소스 코드 설명(클래스별 역할·협력 관계·어디를 고치면 되나): `..\ServoControlWpf_설계문서\ServoControlWpf_소스설명.pdf`
