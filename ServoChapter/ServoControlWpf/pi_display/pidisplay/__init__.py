"""pi_display — Raspberry Pi 모니터용 "카메라 영상 + 카메라(서보 팬) 이동 로그" 프로그램의 패키지.

모듈 지도
    version        VERSION 파일에서 버전 읽기
    servo_spec     펄스폭 ↔ 각도 (WPF ServoSpec 과 같은 공식)
    movement_log   서보 펄스 변화를 시각·각도·변화량과 함께 기록 (메모리 + 파일)
    servo_poller   pigpiod 에 GPW(84) 로 현재 펄스폭을 주기적으로 물어 MovementLog 에 넘김
    frame_hub      카메라가 만든 최신 JPEG 한 장을 서버·화면이 함께 쓰는 보관함
    mjpeg_server   HTTP: /stream.mjpg /snapshot.jpg /status.json /log.txt /
    note_listener  (선택) WPF 가 UDP 로 보내는 메모("WPF +20.0°")를 로그에 붙임
    ui_qt          PyQt5 화면 (800×480)
    camera/        카메라 백엔드 (picamera2 → libcamera-vid → picamera → opencv → fake)
"""
from .version import __version__

__all__ = ["__version__"]
