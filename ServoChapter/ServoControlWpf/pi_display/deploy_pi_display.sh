#!/bin/bash
# WSL → Pi 배포: pi_display 를 Pi 로 복사하고 모니터(DISPLAY=:0)에 띄운다.
#
#   사용법: ./deploy_pi_display.sh [ssh별명=rpi] [--install-deps] [--autostart|--service] [--no-run] [-- pi_display 추가 플래그]
#   예:     ./deploy_pi_display.sh rpi --install-deps --autostart     # 처음 한 번 (apt + 자동 시작 등록)
#           ./deploy_pi_display.sh rpi                                # 그 뒤: 복사 → 재실행 → 상태 확인
#           ./deploy_pi_display.sh rpi -- --fps 15 --quality 60
set -e
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST="rpi"; INSTALL=0; AUTOSTART=""; RUN=1; EXTRA=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-deps) INSTALL=1 ;;
    --autostart) AUTOSTART=desktop ;;
    --service) AUTOSTART=service ;;
    --no-run) RUN=0 ;;
    --) shift; EXTRA=("$@"); break ;;
    -*) echo "알 수 없는 옵션: $1" >&2; exit 2 ;;
    *) HOST="$1" ;;
  esac
  shift
done

echo "[1/5] Pi 사용자 확인 (ssh $HOST)"
INFO="$(ssh -o BatchMode=yes -o ConnectTimeout=5 "$HOST" 'echo "$USER:$HOME"')" || {
  echo "ssh $HOST 에 비밀번호 없이 접속되지 않습니다. tools/setup_rpi_wireless.sh 를 먼저 실행하세요." >&2; exit 1; }
PI_USER="${INFO%%:*}"; PI_HOME="${INFO#*:}"
DEST="$PI_HOME/Source/pi_display"
echo "      $PI_USER @ $DEST"

echo "[2/5] 복사 (rsync)"
ssh "$HOST" "mkdir -p '$DEST'"
rsync -az --delete --exclude __pycache__ --exclude tests --exclude '*.pyc' "$HERE/" "$HOST:$DEST/"
rsync -az "$HERE/../VERSION" "$HOST:$DEST/VERSION"
ssh "$HOST" "chmod +x '$DEST'/*.sh '$DEST'/pi_display.py"

if [[ $INSTALL -eq 1 ]]; then
  echo "[3/5] 패키지 설치 (sudo 비밀번호를 물을 수 있음)"
  ssh -t "$HOST" "bash '$DEST/install_pi_deps.sh'"
else
  echo "[3/5] 패키지 설치 건너뜀 (--install-deps 로 설치)"
fi

if [[ -n "$AUTOSTART" ]]; then
  echo "[4/5] 자동 시작 등록 ($AUTOSTART)"
  if [[ "$AUTOSTART" == "desktop" ]]; then
    ssh "$HOST" "mkdir -p ~/.config/autostart && sed -e 's|@HOME@|$PI_HOME|g' -e 's|@USER@|$PI_USER|g' '$DEST/pi-display.desktop.in' > ~/.config/autostart/pi-display.desktop && echo '      ~/.config/autostart/pi-display.desktop'"
  else
    ssh -t "$HOST" "sed -e 's|@HOME@|$PI_HOME|g' -e 's|@USER@|$PI_USER|g' '$DEST/pi-display.service.in' | sudo tee /etc/systemd/system/pi-display.service >/dev/null && sudo systemctl daemon-reload && sudo systemctl enable pi-display && echo '      systemd pi-display.service (부팅 시 --no-gui 서버)'"
  fi
else
  echo "[4/5] 자동 시작 등록 건너뜀 (--autostart 또는 --service)"
fi

if [[ $RUN -eq 1 ]]; then
  echo "[5/5] 실행"
  # '[p]i_display.py' 로 써야 이 명령을 실행하는 ssh 셸(bash -c "... pi_display.py ...") 자신은 안 잡힌다.
  # 그냥 'pi_display.py' 로 쓰면 자기 셸을 죽여 ssh 가 비정상 종료 → set -e 로 여기서 멈춘다.
  ssh "$HOST" "pkill -f '[p]i_display.py' || true"
  FLAGS="--fullscreen ${EXTRA[*]}"
  # ( … & ) 괄호가 중요: 괄호 없이 "cd … && nohup … &" 로 쓰면 & 가 "cd && nohup" 전체에 걸려 bash 서브셸이
  # python 이 끝날 때까지 남고, 그 서브셸이 ssh 의 출력 파이프를 쥐고 있어 ssh(이 스크립트)가 영원히 안 끝난다.
  # 괄호 안에서 & 를 쓰면 서브셸은 python 을 띄우자마자 끝나고, python 의 입출력은 모두 파일/널로 돌려서 ssh 가 바로 돌아온다.
  ssh "$HOST" "cd '$DEST' && ( DISPLAY=:0 XAUTHORITY=\$HOME/.Xauthority setsid nohup python3 pi_display.py $FLAGS > pi_display.log 2>&1 < /dev/null & )"
  sleep 4
  echo "--- pi_display.log (앞 12줄)"
  ssh "$HOST" "head -12 '$DEST/pi_display.log'"
  echo "--- status.json"
  ssh "$HOST" "curl -s --max-time 3 http://127.0.0.1:8889/status.json | head -c 400; echo"
  PI_IP="$(ssh "$HOST" "hostname -I | awk '{print \$1}'")"
  echo ""
  echo "Pi 모니터에 화면이 떠 있어야 합니다. WPF 에서는 카메라 콤보 'Pi 카메라 (네트워크)', URL http://$PI_IP:8889/stream.mjpg"
  echo "로그 보기: ssh $HOST 'tail -f $DEST/pi_display.log'   /   브라우저: http://$PI_IP:8889/"
else
  echo "[5/5] 실행 건너뜀 (--no-run)"
fi
