#!/bin/bash
# Pi 에서 실행 (deploy_pi_display.sh --install-deps 가 ssh 로 호출): pi_display 에 필요한 패키지 설치 + 카메라 스택 안내
set -e
export DEBIAN_FRONTEND=noninteractive
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
sudo apt-get install -y -qq python3-opencv scrot 2>/dev/null || true

echo "== pigpiod"
if ! systemctl is-active --quiet pigpiod; then
  sudo apt-get install -y -qq pigpio
  sudo systemctl enable --now pigpiod
fi
echo "  pigpiod: $(systemctl is-active pigpiod)"
echo "== 완료"
