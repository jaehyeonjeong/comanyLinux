#!/bin/bash
# Pi 에서 실행 (deploy_pi_display.sh --install-deps 가 ssh 로 호출): pi_display 에 필요한 패키지 설치 + 카메라 스택 안내
#   옵션: --no-models   MobileNet-SSD 모델(약 23 MB) 내려받기 생략 (인식은 OpenCV 내장 HOG 로 사람만)
set -e
export DEBIAN_FRONTEND=noninteractive
MODELS=1
for a in "$@"; do case "$a" in --no-models) MODELS=0 ;; esac; done
echo "== OS: $(grep PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '"')  ($(getconf LONG_BIT)bit)"

echo "== 카메라 스택 확인"
if command -v libcamera-hello >/dev/null 2>&1 || command -v rpicam-hello >/dev/null 2>&1; then
  echo "  libcamera 스택 (libcamera-apps 있음) → picamera2 / libcamera-vid 백엔드"
  STACK=libcamera
elif vcgencmd get_camera 2>/dev/null | grep -q 'supported=1'; then
  echo "  레거시 스택 (start_x=1) → picamera 백엔드"
  STACK=legacy
else
  echo "  확인 불가 — libcamera 패키지를 설치해 봅니다 (Bullseye 후기 이미지 기본)"
  STACK=libcamera
fi

echo "== apt 설치"
sudo apt-get update -qq
sudo apt-get install -y -qq python3-pyqt5 python3-pigpio python3-pil rsync curl
if [[ "$STACK" == "libcamera" ]]; then
  sudo apt-get install -y -qq --no-install-recommends python3-picamera2 libcamera-apps || \
    echo "  (python3-picamera2 를 설치하지 못했습니다 — libcamera-vid 백엔드로 동작합니다)"
else
  sudo apt-get install -y -qq python3-picamera || echo "  (python3-picamera 설치 실패)"
fi
echo "== 녹화(mp4 담기)·인식 패키지"
sudo apt-get install -y -qq ffmpeg || echo "  (ffmpeg 없음 — 녹화는 .h264 원시 파일로 저장됩니다: ffmpeg -i a.h264 -c copy a.mp4 로 변환)"
sudo apt-get install -y -qq python3-opencv python3-numpy || echo "  (python3-opencv 없음 — 인체/사물 인식을 쓸 수 없습니다)"
sudo apt-get install -y -qq scrot 2>/dev/null || true

if [[ $MODELS -eq 1 ]]; then
  echo "== 인식 모델 (MobileNet-SSD, VOC 20종)"
  MD="${XDG_DATA_HOME:-$HOME/.local/share}/pi_display/models"
  mkdir -p "$MD"
  BASE="https://github.com/chuanqi305/MobileNet-SSD/raw/master"
  for f in deploy.prototxt mobilenet_iter_73000.caffemodel; do
    if [[ -s "$MD/$f" ]]; then echo "  있음: $f"; continue; fi
    curl -fsSL --retry 2 -o "$MD/$f.tmp" "$BASE/$f" && mv "$MD/$f.tmp" "$MD/$f" && echo "  받음: $f ($(du -h "$MD/$f" | cut -f1))" \
      || { rm -f "$MD/$f.tmp"; echo "  (내려받기 실패: $f — 인식은 HOG(사람만) 로 동작)"; }
  done
fi

echo "== 녹화 폴더"
mkdir -p "$HOME/Videos/pi_display" && echo "  $HOME/Videos/pi_display  (여유 $(df -h "$HOME" | awk 'NR==2{print $4}'))"

echo "== pigpiod"
if ! systemctl is-active --quiet pigpiod; then
  sudo apt-get install -y -qq pigpio
  sudo systemctl enable --now pigpiod
fi
echo "  pigpiod: $(systemctl is-active pigpiod)"
echo "== 완료"
