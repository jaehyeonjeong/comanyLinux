#!/bin/bash
# WSL 쪽 무선(Wi-Fi) 연결 준비: ssh 키 → ~/.ssh/config 의 Host rpi → Pi 에 키 복사 → 접속 확인 → PC 의 Wi-Fi IP 안내
#
#   사용법:  ./tools/setup_rpi_wireless.sh <Pi 의 Wi-Fi IP 또는 호스트 이름> [Pi 사용자(기본 pi)] [별명(기본 rpi)]
#   예:      ./tools/setup_rpi_wireless.sh 192.168.0.50
#            ./tools/setup_rpi_wireless.sh raspberrypi.local jhjung
#
#   끝나면: WPF 에서 "WSL 터널" 모드(ssh 별명 rpi) 로 [연결] → 설정 없이 바로 동작.
#          "직접 연결" 모드를 쓰려면 Pi 쪽 pigpiod 가 PC 의 Wi-Fi IP 를 허용해야 함 → 마지막에 출력되는 명령 참고.
set -e
PI_HOST="${1:?Pi 의 IP 또는 호스트 이름을 주세요 (예 192.168.0.50)}"
PI_USER="${2:-pi}"
ALIAS="${3:-rpi}"
KEY="$HOME/.ssh/id_ed25519"
CFG="$HOME/.ssh/config"

mkdir -p "$HOME/.ssh"; chmod 700 "$HOME/.ssh"

echo "[1/4] ssh 키"
if [[ -f "$KEY" ]]; then
  echo "  이미 있음: $KEY"
else
  ssh-keygen -t ed25519 -N "" -f "$KEY" -C "wsl-$(hostname)" >/dev/null
  echo "  만듦: $KEY"
fi

echo "[2/4] ~/.ssh/config 에 Host $ALIAS"
touch "$CFG"; chmod 600 "$CFG"
if grep -qE "^Host[[:space:]]+$ALIAS\$" "$CFG"; then
  # 기존 항목의 HostName / User 만 갱신
  awk -v a="$ALIAS" -v h="$PI_HOST" -v u="$PI_USER" '
    $1=="Host" { inblk = ($2==a) }
    inblk && $1=="HostName" { $0="    HostName " h }
    inblk && $1=="User"     { $0="    User " u }
    { print }' "$CFG" > "$CFG.tmp" && mv "$CFG.tmp" "$CFG"
  echo "  갱신: Host $ALIAS → $PI_USER@$PI_HOST"
else
  cat >> "$CFG" <<EOF

Host $ALIAS
    HostName $PI_HOST
    User $PI_USER
    IdentityFile $KEY
    ServerAliveInterval 15
    ConnectTimeout 5
EOF
  echo "  추가: Host $ALIAS → $PI_USER@$PI_HOST"
fi
chmod 600 "$CFG"

echo "[3/4] Pi 에 공개키 복사 (Pi 비밀번호를 한 번 물어봅니다)"
if ssh -o BatchMode=yes -o ConnectTimeout=5 "$ALIAS" true 2>/dev/null; then
  echo "  이미 키 인증이 됩니다"
else
  ssh-copy-id -i "$KEY.pub" "$ALIAS"
fi

echo "[4/4] 확인"
ssh -o BatchMode=yes "$ALIAS" 'echo "  Pi: $(hostname) / $(hostname -I | tr -s " ")"; echo "  pigpiod: $(systemctl is-active pigpiod 2>/dev/null || echo 미설치)"'

# Pi 가 보게 될 PC 주소 = Windows 에서 공유기(기본 게이트웨이)로 나가는 어댑터의 IPv4 (Wi-Fi 든 이더넷이든). WSL 의 172.x 가 아님에 주의
PC_WIFI_IP="$(powershell.exe -NoProfile -Command "[Console]::OutputEncoding=[Text.Encoding]::UTF8; \$r = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1; if (\$r) { (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex \$r.InterfaceIndex | Select-Object -First 1).IPAddress }" 2>/dev/null | tr -d '\r' | tail -1)"

cat <<EOF

완료.
  ▶ WSL 터널 모드: WPF 에서 연결 방식 "WSL 터널", ssh 별명 "$ALIAS" → [연결]. Pi 쪽 추가 설정 없음 (Pi 는 127.0.0.1 접속으로 봄).
  ▶ 직접 연결 모드: Pi 의 pigpiod 가 이 PC 의 LAN 주소(${PC_WIFI_IP:-ipconfig 로 확인}) 를 허용해야 합니다. Pi 를 처음 설정할 때:
        cd ../ServoControl && ./pi_setup.sh $ALIAS ${PC_WIFI_IP:-<PC_WIFI_IP>}
     PC 의 IP 가 바뀌면(공유기 DHCP) 다시 실행해야 하므로, 공유기에서 PC IP 를 고정(DHCP 예약)해 두거나 터널 모드를 쓰세요.
EOF
