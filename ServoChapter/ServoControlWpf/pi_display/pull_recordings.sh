#!/bin/bash
# WSL 에서 실행: Pi 의 녹화·사진 폴더(~/Videos/pi_display)를 rsync 로 **끌어온다** (Pi → WSL).
#
# 왜 pull 인가: WSL2 는 Windows 안의 NAT 뒤에 있어 Pi 가 WSL 로 직접 보낼(push) 수 없다. 반대로 WSL → Pi 는 ssh 별명 rpi 로 늘 열려 있다.
#
#   사용법: ./pull_recordings.sh [ssh별명=rpi] [목적지=~/MyJob/projects/comanyLinux/ServoChapter/recordings] [--src 경로] [--remove-source]
#     --src 경로        ssh 대신 이 로컬 폴더에서 복사 (Pi 없이 시험할 때; 별명 자리는 아무 글자나)
#     --remove-source   복사가 끝난 파일을 Pi 에서 지움 (기본은 남겨 둠 — Pi 쪽 디스크 가드가 정리)
#   규칙: 아직 쓰고 있는 파일(*.part)은 건너뛴다. 받은 파일 목록을 찍고 **마지막 줄에 "PULLED n"** 을 출력한다 (WPF 가 이 줄을 읽음).
#   기록: 목적지/pull.log 에 한 줄 추가.
set -u
HOST="rpi"; DEST="$HOME/MyJob/projects/comanyLinux/ServoChapter/recordings"; SRC=""; REMOVE=0; POS=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --src) SRC="$2"; shift ;;
    --remove-source) REMOVE=1 ;;
    -h|--help) sed -n '2,14p' "$0"; exit 0 ;;
    -*) echo "알 수 없는 옵션: $1" >&2; echo "PULLED 0"; exit 2 ;;
    *) if [[ $POS -eq 0 ]]; then HOST="$1"; elif [[ $POS -eq 1 ]]; then DEST="$1"; fi; POS=$((POS+1)) ;;
  esac
  shift
done
DEST="${DEST/#\~/$HOME}"
# 시험용 별명 "local:/경로" = --src /경로 (WPF 의 ssh 별명 칸에 넣어 Pi 없이 전체 흐름을 돌려 볼 때)
if [[ -z "$SRC" && "$HOST" == local:* ]]; then SRC="${HOST#local:}"; fi
mkdir -p "$DEST" || { echo "목적지 폴더를 만들 수 없습니다: $DEST" >&2; echo "PULLED 0"; exit 1; }

if [[ -n "$SRC" ]]; then
  SRC="${SRC/#\~/$HOME}"
  [[ -d "$SRC" ]] || { echo "원본 폴더가 없습니다: $SRC" >&2; echo "PULLED 0"; exit 1; }
  SRC_SPEC="${SRC%/}/"
  WHERE="$SRC"
else
  PI_HOME="$(ssh -o BatchMode=yes -o ConnectTimeout=5 "$HOST" 'echo $HOME' 2>/dev/null)" || {
    echo "ssh $HOST 에 접속할 수 없습니다 (Pi 가 꺼져 있거나 키가 없음). tools/setup_rpi_wireless.sh 참고." >&2
    echo "PULLED 0"; exit 1; }
  ssh -o BatchMode=yes "$HOST" "mkdir -p '$PI_HOME/Videos/pi_display'" 2>/dev/null || true
  SRC_SPEC="$HOST:$PI_HOME/Videos/pi_display/"
  WHERE="$HOST:$PI_HOME/Videos/pi_display"
fi

OPTS=(-a --partial --itemize-changes --exclude '*.part' --exclude '*.tmp' --exclude 'pull.log')
[[ $REMOVE -eq 1 ]] && OPTS+=(--remove-source-files)
OUT="$(rsync "${OPTS[@]}" "$SRC_SPEC" "$DEST/" 2>&1)"; RC=$?

# ">f" 로 시작하는 줄 = 새로 받았거나 바뀐 파일
NEW=0
while IFS= read -r line; do
  [[ "$line" == \>f* ]] || continue
  NAME="${line#* }"
  echo "받음: $NAME  ($(du -h "$DEST/$NAME" 2>/dev/null | cut -f1))"
  NEW=$((NEW+1))
done <<< "$OUT"
if [[ $RC -ne 0 ]]; then
  echo "rsync 오류 (코드 $RC):" >&2
  echo "$OUT" | grep -v '^>f' | head -5 >&2
fi
echo "$(date '+%Y-%m-%d %H:%M:%S')  $WHERE → $DEST  새 파일 $NEW  (rsync $RC)" >> "$DEST/pull.log"
echo "원본 $WHERE → $DEST  (총 $(ls "$DEST" 2>/dev/null | grep -Ec '\.(mp4|h264|avi|jpg)$')개 보관)"
echo "PULLED $NEW"
exit $RC
