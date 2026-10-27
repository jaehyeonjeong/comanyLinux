#!/usr/bin/env python3
"""pi_display — Raspberry Pi 모니터에 "카메라 영상 + 카메라(서보 팬) 이동 로그" 를 띄우고, 같은 영상을 HTTP MJPEG 로 내보낸다.
WPF 의 요청으로 녹화(H.264 하드웨어 / MJPEG AVI)·5 MP 사진·카메라 설정·OpenCV 인식도 한다.

    Pi 에서:   python3 pi_display.py --fullscreen
    PC(WSL):   python3 pi_display.py --no-gui --fake-camera --pigpio-host 127.0.0.1     # Pi 없이 시험

WPF 는 카메라 콤보의 "Pi 카메라 (네트워크)" 로 http://<Pi>:8889/stream.mjpg 를 받고, 같은 포트의 /record/* /snapshot/save
/camera/settings /detect/* 로 조작한다. 녹화 파일은 --rec-dir(기본 ~/Videos/pi_display) 에 쌓이고 WSL 의 pull_recordings.sh 가 가져간다.
서보 이동 로그는 pigpiod 에 GPW(84) 로 현재 펄스폭을 50 ms 마다 물어 바뀔 때만 기록한다 (누가 움직였든 서보가 실제로 받은 값).
"""
import argparse
import json
import os
import signal
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pidisplay import __version__                      # noqa: E402
from pidisplay.camera import NAMES as CAMERA_NAMES, select_backend   # noqa: E402
from pidisplay.camera_settings import CameraManager, CameraSettings, RECORD_MODES, default_settings_path   # noqa: E402
from pidisplay.detector import DEFAULT_MODEL_DIR, Detector   # noqa: E402
from pidisplay.frame_hub import FrameHub               # noqa: E402
from pidisplay.mjpeg_server import MjpegServer, ServerContext   # noqa: E402
from pidisplay.movement_log import MovementLog         # noqa: E402
from pidisplay.recorder import DEFAULT_REC_DIR, Recorder   # noqa: E402
from pidisplay.servo_poller import ServoPoller         # noqa: E402
from pidisplay.servo_spec import ServoSpec             # noqa: E402

DEFAULT_LOG = os.path.join(os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share")), "pi_display", "moves.log")


def load_calib(arg: "str|None", path: str):
    """보정값 결정: --calib 명시 > 저장 파일(WPF 가 보낸 것) > HS-311 공식값. (spec, 출처 설명)"""
    if arg:
        return ServoSpec.parse(arg), "--calib"
    try:
        with open(path, encoding="utf-8") as f:
            return ServoSpec.parse(json.load(f)["calib"]), f"저장값 {path}"
    except (OSError, ValueError, KeyError, TypeError):
        return ServoSpec(), "기본값"


def parse_args(argv=None):
    ap = argparse.ArgumentParser(description="Pi 카메라 영상 + 서보 이동 로그 화면 / MJPEG 서버 / 녹화·사진·인식")
    ap.add_argument("--version", action="version", version=f"pi_display {__version__}")
    g = ap.add_argument_group("카메라")
    g.add_argument("--camera", choices=["auto"] + CAMERA_NAMES, default="auto")
    g.add_argument("--fake-camera", action="store_true", help="= --camera fake (테스트 패턴)")
    g.add_argument("--device", default="", help="opencv 백엔드의 장치 번호 (기본 0)")
    g.add_argument("--width", type=int, default=640, help="미리보기 크기")
    g.add_argument("--height", type=int, default=480)
    g.add_argument("--fps", type=int, default=10, help="미리보기 fps (녹화 fps 는 녹화 모드가 정함)")
    g.add_argument("--quality", type=int, default=70, help="JPEG 품질 1~100")
    g.add_argument("--record-mode", choices=list(RECORD_MODES), default=None,
                   help="시작 시 녹화 모드 (지정하지 않으면 camera.json 에 저장된 값, 기본 1080p30)")
    g.add_argument("--camera-settings", default=default_settings_path(), help="카메라 설정 저장 파일")
    g = ap.add_argument_group("녹화·사진")
    g.add_argument("--rec-dir", default=DEFAULT_REC_DIR, help="녹화·사진 저장 폴더")
    g.add_argument("--rec-max-minutes", type=float, default=60, help="한 번에 최대 녹화 시간(분), 0 = 제한 없음")
    g.add_argument("--rec-max-gb", type=float, default=8.0, help="폴더 상한(GB) — 넘으면 오래된 파일부터 삭제")
    g.add_argument("--rec-bitrate", type=int, default=None, help="H.264 비트레이트(bit/s), 기본은 모드별 4~8 Mbps")
    g.add_argument("--no-record", action="store_true", help="녹화·사진 API 끄기")
    g = ap.add_argument_group("인식 (OpenCV)")
    g.add_argument("--detect", action="store_true", help="시작할 때부터 인식 켬")
    g.add_argument("--detect-every", type=int, default=3, help="몇 장마다 한 번 추론할지")
    g.add_argument("--detect-conf", type=float, default=0.5, help="신뢰도 문턱 0~1")
    g.add_argument("--model-dir", default=DEFAULT_MODEL_DIR, help="MobileNet-SSD 모델 폴더 (없으면 HOG)")
    g.add_argument("--no-detect", action="store_true", help="인식 API 끄기")
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
    g.add_argument("--calib", default=None,
                   help="최소펄스,중립,최대펄스,최소각,최대각 (WPF 보정값과 같게). 생략하면 --calib-file 에 저장된 값, 없으면 HS-311 공식값 "
                        "1100,1500,1900,-40,40. WPF 가 Pi 카메라로 붙으면 자기 보정값을 /servo/calib 로 보내 자동으로 맞춤")
    g.add_argument("--calib-file", default=os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")), "pi_display", "servo.json"),
                   help="WPF 가 보낸 보정값을 저장해 다음 실행에도 쓰는 파일")
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
    spec, calib_from = load_calib(a.calib, a.calib_file)
    print(f"pi_display v{__version__}  보정 {spec} ({calib_from})  GPIO{a.gpio}  pigpiod {a.pigpio_host}:{a.pigpio_port}", flush=True)

    def save_calib(new_spec):
        try:
            os.makedirs(os.path.dirname(a.calib_file) or ".", exist_ok=True)
            with open(a.calib_file, "w", encoding="utf-8") as f:
                json.dump({"calib": new_spec.to_calib()}, f)
        except OSError:
            pass

    log = MovementLog(spec, None if a.no_log_file else a.log_file, version=__version__)
    hub = FrameHub()
    poller = ServoPoller(log, a.pigpio_host, a.pigpio_port, a.gpio, a.poll_ms, prefer_module=not a.no_pigpio_module)
    poller.start()

    def overlay() -> str:
        p, ang = log.current_pulse, log.current_angle
        return f"servo {ang:+.1f} deg {p} us" if p else ("servo off" if p == 0 else "servo ?")

    # 카메라 설정(camera.json) → 카메라 관리자
    settings = CameraSettings.load(a.camera_settings)
    if a.record_mode:
        settings = CameraSettings.from_dict({"record_mode": a.record_mode}, settings)
    recorder = None
    camera = CameraManager(select_backend, a.camera,
                           dict(hub=hub, width=a.width, height=a.height, fps=a.fps, quality=a.quality,
                                overlay=overlay, device=a.device),
                           settings, a.camera_settings, is_busy=lambda: bool(recorder and recorder.recording))
    try:
        tried = camera.start()
    except RuntimeError as ex:
        print(ex, file=sys.stderr, flush=True)
        poller.stop()
        return 2
    for line in tried:
        print("카메라:", line, flush=True)
    cam = camera.backend
    print(f"카메라 설정: 녹화 모드 {settings.record_mode}  반전 h={settings.hflip} v={settings.vflip}  "
          f"({a.camera_settings})" + (f"  [{cam.detail}]" if getattr(cam, 'detail', '') else ""), flush=True)

    # 녹화기 — 백엔드가 바뀌어도(설정 변경으로 재시작) 최신 백엔드를 보도록 프록시
    class _BackendProxy:
        def __getattr__(self, item):
            return getattr(camera.backend, item)

    if not a.no_record:
        recorder = Recorder(hub, _BackendProxy(), a.rec_dir, a.rec_max_minutes, a.rec_max_gb,
                            bitrate=a.rec_bitrate, on_event=lambda kind, text: log.event(text, kind))
        print(f"녹화 폴더: {a.rec_dir}  (여유 {recorder.disk()['free_mb']} MB, 최대 {a.rec_max_minutes:g}분/회, {a.rec_max_gb:g} GB 상한)", flush=True)

    detector = None
    if not a.no_detect:
        detector = Detector(every=a.detect_every, conf=a.detect_conf, model_dir=a.model_dir, enabled=a.detect, quality=a.quality)
        hub.set_filter(detector.annotate)
        print("인식:", f"{detector.model} (켜짐)" if detector.enabled else (
            f"{detector.model} 준비됨 (꺼짐 — WPF 에서 켬)" if detector.available else detector.error), flush=True)

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
        ctx = ServerContext(hub, log, poller, __version__, a.gpio, recorder=recorder, detector=detector, camera=camera,
                            on_calib=save_calib)
        try:
            server = MjpegServer(ctx, a.http_bind, a.http_port)
            server.start()
            print(f"HTTP: http://{a.http_bind}:{server.port}/  (stream.mjpg · snapshot.jpg · status.json · log.txt · "
                  f"record/* · snapshot/save · recordings.json · camera/settings · detect/*)", flush=True)
        except OSError as ex:
            print(f"HTTP 포트 {a.http_port} 를 열지 못함: {ex}", file=sys.stderr, flush=True)
            camera.stop(); poller.stop()
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
            rc = run_ui(hub, log, poller, a.http_port, __version__, a.gpio, fullscreen=not a.windowed,
                        recorder=recorder, detector=detector)
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
        if recorder:
            recorder.close()            # 녹화 중이면 파일을 닫고 .part → 최종 이름 (전송 스크립트가 가져갈 수 있게)
        if server:
            server.stop()
        if notes:
            notes.stop()
        if detector:
            detector.stop()
        camera.stop()
        poller.stop()
    return rc


if __name__ == "__main__":
    sys.exit(main())
