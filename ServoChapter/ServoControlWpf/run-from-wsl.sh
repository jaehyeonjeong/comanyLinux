#!/bin/bash
# WSL 셸에서 ServoControlWpf(Windows WPF 앱)를 띄우는 실행기
#
#   사용법:  ./run-from-wsl.sh            빌드된 Release 실행 파일을 띄움 (없으면 Debug)
#            ./run-from-wsl.sh build      Windows 의 dotnet.exe 로 먼저 빌드한 뒤 띄움
#            ./run-from-wsl.sh emu [포트] 가짜 pigpiod 콘솔(pigpio-emu)을 이 터미널에서 실행
#
#   원리: WSL 의 "Windows interop" 으로 .exe 를 실행하면 Windows 가 그 프로그램을 띄운다.
#         창은 Windows 화면에 뜨고, 네트워크·설정도 Windows 쪽을 쓴다 (WPF 는 리눅스에서 직접 돌지 않음).
#         리눅스 파일시스템(~/…) 에 있는 .exe 는 실행 비트(chmod +x)가 있어야 한다.
set -e
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$HERE/ServoControlWpf"
EMU_DIR="$HERE/PigpioEmulatorCli"

if [[ "$1" == "emu" ]]; then
  # Windows 쪽 가짜 pigpiod (Windows 프로세스로 실행됨). WSL 안에서 리눅스 프로세스로 띄우려면 tools/fake_pigpiod.py 를 쓴다.
  EXE=""
  for cand in "$EMU_DIR/bin/Release/net8.0/pigpio-emu.exe" "$EMU_DIR/bin/Debug/net8.0/pigpio-emu.exe"; do
    [[ -f "$cand" ]] && { EXE="$cand"; break; }
  done
  [[ -n "$EXE" ]] || { echo "pigpio-emu 가 빌드되어 있지 않습니다. ./run-from-wsl.sh build 를 먼저 실행하세요."; exit 1; }
  chmod +x "$EXE"
  exec "$EXE" "${2:-8888}" "${3:-127.0.0.1}"
fi

if [[ "$1" == "fakepi" ]]; then
  # WSL 자체를 "가짜 Pi" 로: 리눅스 프로세스로 pigpiod 흉내 (Windows 의 WPF 는 직접 연결 127.0.0.1:8888 로 접속 가능)
  exec python3 "$HERE/tools/fake_pigpiod.py" --port "${2:-8888}" --bind "${3:-0.0.0.0}"
fi

if [[ "$1" == "build" ]]; then
  command -v dotnet.exe >/dev/null || { echo "Windows 의 dotnet.exe 를 찾을 수 없습니다 (interop 또는 .NET SDK 확인)."; exit 1; }
  WIN_SLN="$(wslpath -w "$HERE/ServoControlWpf.sln")"
  echo "빌드: dotnet.exe build \"$WIN_SLN\" -c Release"
  dotnet.exe build "$WIN_SLN" -c Release -nologo -v q
fi

EXE=""
for cand in "$APP_DIR/bin/Release/net8.0-windows/ServoControlWpf.exe" "$APP_DIR/bin/Debug/net8.0-windows/ServoControlWpf.exe"; do
  [[ -f "$cand" ]] && { EXE="$cand"; break; }     # Release 를 먼저 찾고, 없으면 Debug
done
[[ -n "$EXE" ]] || { echo "실행 파일이 없습니다. ./run-from-wsl.sh build 로 빌드하세요."; exit 1; }
chmod +x "$EXE"
echo "실행: $EXE"
echo "      (Windows 경로: $(wslpath -w "$EXE"))"
# setsid + nohup: 이 터미널을 닫아도 앱이 살아 있고, 터미널은 바로 돌아온다
setsid nohup "$EXE" >/dev/null 2>&1 < /dev/null &
sleep 1
echo "창이 Windows 화면에 떴습니다. 연결 방식 '에뮬레이터' 로 Pi 없이 시험할 수 있습니다."
