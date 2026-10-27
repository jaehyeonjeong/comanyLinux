#!/bin/bash
# WSL에서 한 번만 실행: Pi에 pigpio를 설치하고, PC(이더넷)에서 원격 접속할 수 있게 설정
#   사용법: ./pi_setup.sh                (기본: 접속 별명 rpi, 허용할 PC 주소 192.168.10.1)
#           ./pi_setup.sh rpi 192.168.10.1
set -e
HOST="${1:-rpi}"
PC_IP="${2:-192.168.10.1}"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT
cat > "$TMP" << REMOTE
#!/bin/bash
set -e
echo "[1/3] pigpio 설치"
sudo apt-get update
sudo apt-get install -y pigpio

echo "[2/3] 원격 접속 허용 (PC $PC_IP 와 Pi 자신만 허용)"
# 기본 pigpiod 서비스는 '-l'(Pi 내부만 허용)로 실행되므로, 허용 주소를 지정해 덮어씀
sudo mkdir -p /etc/systemd/system/pigpiod.service.d
printf '[Service]\nExecStart=\nExecStart=/usr/bin/pigpiod -n 127.0.0.1 -n $PC_IP\n' \\
  | sudo tee /etc/systemd/system/pigpiod.service.d/remote.conf > /dev/null

echo "[3/3] pigpiod 재시작 및 부팅 시 자동 실행"
sudo systemctl daemon-reload
sudo systemctl enable pigpiod
sudo systemctl restart pigpiod
sleep 1
echo "pigpiod 상태: \$(systemctl is-active pigpiod)"
REMOTE

scp -q "$TMP" "$HOST:/tmp/servo_pi_setup.sh"
ssh -t "$HOST" 'bash /tmp/servo_pi_setup.sh; rc=$?; rm -f /tmp/servo_pi_setup.sh; exit $rc'

echo ""
echo "완료. 이제 WSL에서 ServoControl을 실행하고 [연결]을 누르세요. (포트 8888)"
