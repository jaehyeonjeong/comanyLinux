#!/bin/bash
# WSL에서 실행: 연결된 Raspberry Pi의 하드웨어·OS·개발 환경 정보를 모아 pi_info.txt 로 저장
#   사용법: ./pi_info.sh            (기본 접속 별명: rpi)
#           ./pi_info.sh 다른별명
HOST="${1:-rpi}"
OUT="pi_info.txt"

ssh "$HOST" 'bash -s' > "$OUT" 2>&1 << 'REMOTE'
section() { echo; echo "===== $1 ====="; }
run()     { command -v "${1%% *}" >/dev/null 2>&1 && eval "$1" || echo "($1: 명령 없음)"; }

section "수집 시각"
date '+%Y-%m-%d %H:%M:%S %Z'

section "보드 모델"
tr -d '\0' < /proc/device-tree/model; echo
grep -E '^(Revision|Hardware)' /proc/cpuinfo

section "CPU"
run "lscpu | grep -E 'Architecture|Model name|^CPU\(s\)|Thread|Core|Socket|CPU max MHz|CPU min MHz|Byte Order'"
echo "현재 클럭: $(vcgencmd measure_clock arm 2>/dev/null)"
echo "설정 클럭: $(vcgencmd get_config arm_freq 2>/dev/null)"
echo "OS 비트수: $(getconf LONG_BIT)bit"

section "메모리"
free -h
echo "GPU 메모리: $(vcgencmd get_mem gpu 2>/dev/null)"
echo "ARM 메모리: $(vcgencmd get_mem arm 2>/dev/null)"

section "저장장치"
lsblk -o NAME,SIZE,TYPE,MOUNTPOINT
df -h / /boot 2>/dev/null

section "온도·전원 상태"
vcgencmd measure_temp 2>/dev/null
echo "throttled: $(vcgencmd get_throttled 2>/dev/null)   (0x0 = 저전압·과열 이력 없음)"
for r in core sdram_c sdram_i sdram_p; do echo "volt $r: $(vcgencmd measure_volts $r 2>/dev/null)"; done

section "OS·커널"
grep -E '^(PRETTY_NAME|VERSION_CODENAME)' /etc/os-release
uname -a
echo "펌웨어: $(vcgencmd version 2>/dev/null | head -1)"

section "네트워크"
ip -br addr
echo "무선 장치:"; iw dev 2>/dev/null | grep -E 'Interface|ssid' || echo "(iw 없음)"
echo "블루투스:"; (hciconfig 2>/dev/null | head -1) || echo "(없음)"

section "USB 장치"
run "lsusb"

section "디스플레이"
DISPLAY=:0 xrandr 2>/dev/null | grep -E ' connected|\*' || echo "(xrandr 정보 없음)"

section "인터페이스 활성화 (0=켜짐, 1=꺼짐)"
for i in i2c spi serial_hw ssh vnc; do echo "$i: $(sudo -n raspi-config nonint get_$i 2>/dev/null || echo '?')"; done
ls /dev/i2c-* /dev/spidev* /dev/gpiochip* 2>/dev/null

section "GPIO 핀 배치 (pinout)"
run "pinout --monochrome"

section "개발 환경"
echo "gcc:   $(gcc --version 2>/dev/null | head -1)"
echo "cmake: $(cmake --version 2>/dev/null | head -1)"
echo "Qt:    $(qmake -query QT_VERSION 2>/dev/null || dpkg-query -W -f='${Version}' qtbase5-dev 2>/dev/null)"
echo "python: $(python3 --version 2>&1)"
echo "pigpio: $(dpkg-query -W -f='${Version}' pigpio 2>/dev/null || echo '미설치')"
echo "pigpiod 상태: $(systemctl is-active pigpiod 2>/dev/null)"
echo "libgpiod: $(dpkg-query -W -f='${Version}' gpiod 2>/dev/null || echo '미설치')"
REMOTE

echo "저장 완료: $(pwd)/$OUT"
echo "내용을 복사해서 Claude에게 보내주세요:  cat $OUT"
