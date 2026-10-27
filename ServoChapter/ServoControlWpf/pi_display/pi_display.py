#!/usr/bin/env python3
"""pi_display — Raspberry Pi 모니터에 "카메라 영상 + 카메라(서보 팬) 이동 로그" 를 띄우고, 같은 영상을 HTTP MJPEG 로 내보낸다.

    Pi 에서:   python3 pi_display.py --fullscreen
    PC(WSL):   python3 pi_display.py --no-gui --fake-camera --pigpio-host 127.0.0.1     # Pi 없이 시험

WPF 는 카메라 콤보의 "Pi 카메라 (네트워크)" 로 http://<Pi>:8889/stream.mjpg 를 받는다.
서보 이동 로그는 pigpiod 에 GPW(84) 로 현재 펄스폭을 50 ms 마다 물어 바뀔 때만 기록한다 (누가 움직였든 서보가 실제로 받은 값).
"""
import argparse
import os
import signal
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pidisplay import __version__                      # noqa: E402
from pidisplay.camera import NAMES as CAMERA_NAMES, select_backend   # noqa: E402
from pidisplay.frame_hub import FrameHub               # noqa: E402
from pidisplay.mjpeg_server import MjpegServer, ServerContext   # noqa: E402
from pidisplay.movement_log import MovementLog         # noqa: E402
from pidisplay.servo_poller import ServoPoller         # noqa: E402
from pidisplay.servo_spec import ServoSpec             # noqa: E402

DEFAULT_LOG = os.path.join(os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share")), "pi_display", "moves.log")


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description="Pi 카메라 영상 + 서보 이동 로그 화면 / MJPEG 서버")
    ap.add_argument("--version", action="version", version=f"pi_display {__version__}")
    g = ap.add_argument_group("카메라")
    g.add_argument("--camera", choices=["auto"] + CAMERA_NAMES, default="auto")
    g.add_argument("--fake-camera", action="store_true", help="= --camera fake (테스트 패턴)")
    g.add_argument("--device", default="", help="opencv 백엔드의 장치 번호 (기본 0)")
    g.add_argument("--width", type=int, default=640)
    g.add_argument("--height", type=int, default=480)
    g.add_argument("--fps", type=int, default=10)
    g.add_argument("--quality", type=int, default=70, help="JPEG 품질 1~100")
    g = ap.add_argument_group("HTTP")
    g.add_argument("--http-port", type=int, default=8889)
    g.add_argument("--http-bind", default="0.0.0.0")
    g.add_argument("--no-http", action="store_true")
    g = ap.add_argument_group("화면")
    m = g.add_mutually_exclusive_group()
    m.add_argument("--no-gui", action="store_true", help="화면 없이 서버·로그만 (로그는 stdout)")
    m.add_argument("--windowed", action="store_true", help="창 모드 (개발용)")
    m.add_argument("--fullscreen", action="store_true", help="전체 화면 (Pi 기본)")
    g = ap.add_argument_group("서보 (pigpiod)")
    g.add_argument("--pigpio-host", default="127.0.0.1")
    g.add_argument("--pigpio-port", type=int, default=8888)
    g.add_argument("--gpio", type=int, default=18)
    g.add_argument("--poll-ms", type=int, default=50)
    g.add_argument("--calib", default="1100,1500,1900,-40,40", help="최소펄스,중립,최대펄스,최소각,최대각 (WPF 보정값과 같게)")
    g.add_argument("--no-pigpio-module", action="store_true", help="pigpio 파이썬 모듈 대신 소켓(GPW)만 사용")
    g = ap.add_argument_group("로그·메모")
    g.add_argument("--log-file", default=DEFAULT_LOG)
    g.add_argument("--no-log-file", action="store_true")
    g.add_argument("--note-port", type=int, default=8890, help="WPF 메모(UDP) 수신 포트")
    g.add_argument("--no-notes", action="store_true")
    return ap.parse_args(argv)


def main(argv=None) -> int:
    a = parse_args(argv)
    if a.fake_camera:
        a.camera = "fake"
    spec = ServoSpec.parse(a.calib)
    print(f"pi_display v{__version__}  보정 {spec}  GPIO{a.gpio}  pigpiod {a.pigpio_host}:{a.pigpio_port}", flush=True)

    log = MovementLog(spec, None if a.no_log_file else a.log_file, version=__version__)
    hub = FrameHub()
    poller = ServoPoller(log, a.pigpio_host, a.pigpio_port, a.gpio, a.poll_ms, prefer_module=not a.no_pigpio_module)
    poller.start()

    def overlay() -> str:
        p, ang = log.current_pulse, log.current_angle
        return f"servo {ang:+.1f} deg {p} us" if p else ("servo off" if p == 0 else "servo ?")

    try:
        cam, tried = select_backend(a.camera, hub=hub, width=a.width, height=a.height, fps=a.fps,
                                    quality=a.quality, overlay=overlay, device=a.device)
    except RuntimeError as ex:
        print(ex, file=sys.stderr, flush=True)
        poller.stop()
        return 2
    for line in tried:
        print("카메라:", line, flush=True)

    notes = None
    if not a.no_notes:
        try:
            from pidisplay.note_listener import NoteListener
            notes = NoteListener(log, a.http_bind, a.note_port)
            notes.start()
            print(f"메모 수신: UDP {a.note_port}", flush=True)
        except OSError as ex:
            print(f"메모 수신 포트 {a.note_port} 를 열지 못함: {ex}", flush=True)

    server = None
    if not a.no_http:
        ctx = ServerContext(hub, log, poller, __version__, a.gpio)
        try:
            server = MjpegServer(ctx, a.http_bind, a.http_port)
            server.start()
            print(f"HTTP: http://{a.http_bind}:{server.port}/  (stream.mjpg · snapshot.jpg · status.json · log.txt)", flush=True)
        except OSError as ex:
            print(f"HTTP 포트 {a.http_port} 를 열지 못함: {ex}", file=sys.stderr, flush=True)
            cam.stop(); poller.stop()
            return 3

    def print_entry(e):
        print("로그:", e.text(), flush=True)

    rc = 0
    try:
        use_gui = not a.no_gui
        if use_gui:
            try:
                from pidisplay.ui_qt import run_ui
            except ImportError as ex:
                print(f"PyQt5 를 불러올 수 없어 화면 없이 실행합니다 ({ex}). apt install python3-pyqt5", flush=True)
                use_gui = False
        if use_gui:
            log.add_listener(print_entry)
            rc = run_ui(hub, log, poller, a.http_port, __version__, a.gpio, fullscreen=not a.windowed)
        else:
            log.add_listener(print_entry)
            for e in log.recent(20):
                print_entry(e)
            print("화면 없음 — Ctrl+C 로 종료", flush=True)
            stop = {"v": False}
            signal.signal(signal.SIGTERM, lambda *_: stop.__setitem__("v", True))
            try:
                while not stop["v"]:
                    time.sleep(0.5)
            except KeyboardInterrupt:
                pass
    finally:
        print("종료 중…", flush=True)
        if server:
            server.stop()
        if notes:
            notes.stop()
        cam.stop()
        poller.stop()
    return rc


if __name__ == "__main__":
    sys.exit(main())
