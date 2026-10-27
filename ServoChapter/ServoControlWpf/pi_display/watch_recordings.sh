#!/bin/bash
# WSL 에서 실행: 일정 간격으로 pull_recordings.sh 를 돌려 Pi 의 새 녹화·사진을 자동으로 가져온다.
# WPF 가 꺼져 있어도 동작하도록 **systemd user 서비스**(sudo 불필요)로 등록할 수 있다.
#
#   사용법: ./watch_recordings.sh [ssh별명=rpi] [목적지] [--interval 30] [--src 경로] [--once]
#           ./watch_recordings.sh [별명] [목적지] --install     # ~/.config/systemd/user/pi-recordings-watch.service 등록 + 시작
#           ./watch_recordings.sh --status | --uninstall | --log
#   --once   한 번만 가져오고 끝 (시험용)
#   systemd 가 없으면 --install 은 nohup 백그라운드 루프로 대신 띄운다 (pid 파일 ~/.cache/pi_display/watch.pid).
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST="rpi"; DEST="$HOME/MyJob/projects/comanyLinux/ServoChapter/recordings"; INTERVAL=30; SRC=""; ONCE=0; ACTION=run; POS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --interval) INTERVAL="$2"; shift ;;
    --src) SRC="$2"; shift ;;
    --once) ONCE=1 ;;
    --install) ACTION=install ;;
    --uninstall) ACTION=uninstall ;;
    --status) ACTION=status ;;
    --log) ACTION=log ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    -*) echo "알 수 없는 옵션: $1" >&2; exit 2 ;;
    *) if [[ $POS -eq 0 ]]; then HOST="$1"; elif [[ $POS -eq 1 ]]; then DEST="$1"; fi; POS=$((POS+1)) ;;
  esac
  shift
done
DEST="${DEST/#\~/$HOME}"
UNIT_DIR="$HOME/.config/systemd/user"; UNIT="pi-recordings-watch.service"
PIDFILE="$HOME/.cache/pi_display/watch.pid"
SRC_ARGS=(); [[ -n "$SRC" ]] && SRC_ARGS=(--src "$SRC")

have_systemd() { command -v systemctl >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; }

case "$ACTION" in
  run)
    echo "감시 시작: $HOST → $DEST  (간격 ${INTERVAL}s$([[ -n "$SRC" ]] && echo ", 로컬 원본 $SRC"))"
    while true; do
      bash "$HERE/pull_recordings.sh" "$HOST" "$DEST" "${SRC_ARGS[@]}" | grep -E '^(받음|PULLED|rsync)' | grep -v '^PULLED 0$' || true
      [[ $ONCE -eq 1 ]] && break
      sleep "$INTERVAL"
    done
    ;;
  install)
    mkdir -p "$DEST"
    if have_systemd; then
      mkdir -p "$UNIT_DIR"
      cat > "$UNIT_DIR/$UNIT" <<EOF
[Unit]
Description=pi_display 녹화·사진 가져오기 (Pi → WSL, ${INTERVAL}s 마다)
After=network-online.target

[Service]
Type=simple
ExecStart=/bin/bash $HERE/watch_recordings.sh $HOST $DEST --interval $INTERVAL ${SRC_ARGS[*]}
Restart=always
RestartSec=10

[Install]
WantedBy=default.target
EOF
      systemctl --user daemon-reload
      systemctl --user enable --now "$UNIT"
      echo "등록됨: $UNIT_DIR/$UNIT"
      systemctl --user --no-pager --lines=3 status "$UNIT" || true
      echo "(WSL 이 켜져 있는 동안 동작. 부팅 때도 돌리려면 Windows 에서: wsl -d <배포판> -e true 를 시작 프로그램에)"
    else
      mkdir -p "$(dirname "$PIDFILE")"
      if [[ -f "$PIDFILE" ]] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
        echo "이미 돌고 있음 (pid $(cat "$PIDFILE"))"; exit 0
      fi
      setsid nohup bash "$HERE/watch_recordings.sh" "$HOST" "$DEST" --interval "$INTERVAL" "${SRC_ARGS[@]}" \
        > "$HOME/.cache/pi_display/watch.log" 2>&1 < /dev/null &
      echo $! > "$PIDFILE"; disown
      echo "systemd 없음 → 백그라운드 루프 시작 (pid $!, 로그 ~/.cache/pi_display/watch.log)"
    fi
    ;;
  uninstall)
    if have_systemd && [[ -f "$UNIT_DIR/$UNIT" ]]; then
      systemctl --user disable --now "$UNIT" || true
      rm -f "$UNIT_DIR/$UNIT"; systemctl --user daemon-reload
      echo "해제됨: $UNIT"
    fi
    if [[ -f "$PIDFILE" ]]; then
      kill "$(cat "$PIDFILE")" 2>/dev/null && echo "백그라운드 루프 종료"; rm -f "$PIDFILE"
    fi
    ;;
  status)
    if have_systemd && [[ -f "$UNIT_DIR/$UNIT" ]]; then
      echo "서비스: $(systemctl --user is-active "$UNIT" 2>/dev/null)  ($(systemctl --user is-enabled "$UNIT" 2>/dev/null))"
      systemctl --user --no-pager --lines=5 status "$UNIT" 2>/dev/null | sed -n '1,12p' || true
    elif [[ -f "$PIDFILE" ]] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
      echo "백그라운드 루프 동작 중 (pid $(cat "$PIDFILE"))"
    else
      echo "감시 서비스 없음 — ./watch_recordings.sh --install 로 등록"
    fi
    if [[ -f "$DEST/pull.log" ]]; then
      echo "--- 최근 가져오기 ($DEST/pull.log)"; tail -5 "$DEST/pull.log"
    fi
    echo "보관 중: $(ls "$DEST" 2>/dev/null | grep -Ec '\.(mp4|h264|avi|jpg)$')개  ($DEST)"
    ;;
  log)
    tail -20 "$DEST/pull.log" 2>/dev/null || echo "아직 기록 없음"
    ;;
esac
