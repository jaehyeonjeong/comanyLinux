"""HTTP 서버 (표준 라이브러리만): WPF 와 브라우저가 Pi 카메라를 보고 상태를 읽고, 녹화·사진·설정·인식을 조작하는 창구.

보기 (GET)
    /stream.mjpg        multipart/x-mixed-replace; boundary=frame — 파트마다 Content-Type·Content-Length
    /snapshot.jpg       최신 한 장 (미리보기 해상도)
    /status.json        version, camera{…}, servo{…}, record{…}, detect{…}, settings{…}, log[최근 20]
    /log.txt            메모리에 있는 로그 전체 (텍스트)
    /                   <img src=stream.mjpg> 한 줄짜리 확인용 페이지
녹화·사진
    GET  /record/status            {recording, kind, file, seconds, bytes, frames, dir, disk{free_mb,…}, last}
    POST /record/start[?name=…]    시작 (이미 녹화 중 409, 디스크 부족 507)
    POST /record/stop              정지 → {name, bytes, seconds, frames, kind}
    POST /snapshot/save            5 MP 정지 사진을 녹화 폴더에 저장 → {name, width, height, bytes, note}
    GET  /recordings.json          [{name, bytes, mtime, kind}] (최신 먼저, *.part 제외)
    GET  /recordings/<name>        파일 내려받기 (ssh 가 없는 환경용; 평소 전송은 WSL 의 pull_recordings.sh)
카메라 설정
    GET  /camera/settings          {settings{record_mode,hflip,vflip,brightness,contrast,saturation,awb}, record_modes, awb_modes, …}
    POST /camera/settings          JSON 본문(또는 쿼리) 의 키만 바꿈 → {settings, restarted, applied} (녹화 중 모드/반전 변경 409)
인식
    GET  /detect/status            {available, enabled, model, fps, objects[…], summary}
    POST /detect/on | /detect/off | /detect/config?every=3&conf=0.5
서보 보정값 (각도 계산 공식을 WPF 와 같게)
    GET  /servo/calib              {calib: "600,1500,2400,-80,80", text}
    POST /servo/calib              {calib: "…"} → 이동 로그의 각도 계산을 바꾸고 파일에 저장 (status.json 의 servo.calib 로도 보임)
오류는 모두 JSON {"error": "…"} + 4xx/5xx. 인증은 없다 (LAN 전용).

스트림은 항상 FrameHub 의 "최신 1장" 만 보내므로 느린 클라이언트가 있어도 밀리지 않는다.
"""
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Optional
from urllib.parse import parse_qs, unquote, urlsplit

from .errors import ApiError
from .frame_hub import FrameHub
from .movement_log import MovementLog
from .servo_spec import ServoSpec


class ServerContext:
    def __init__(self, hub: FrameHub, log: MovementLog, poller=None, version: str = "", gpio: int = 18,
                 recorder=None, detector=None, camera=None, on_calib=None):
        self.hub, self.log, self.poller, self.version, self.gpio = hub, log, poller, version, gpio
        self.recorder, self.detector, self.camera = recorder, detector, camera
        self.on_calib = on_calib            # 보정값이 바뀌면 호출 (파일 저장)
        self.clients = 0
        self.started = time.time()
        self._lock = threading.Lock()

    def set_calib(self, text: str) -> dict:
        try:
            spec = ServoSpec.parse(text)
        except ValueError as ex:
            raise ApiError(str(ex), 400)
        changed = self.log.set_spec(spec) is not None
        if changed and self.on_calib:
            try:
                self.on_calib(spec)
            except Exception:
                pass
        return {"calib": spec.to_calib(), "text": str(spec), "changed": changed}

    def add_client(self, n: int) -> None:
        with self._lock:
            self.clients += n

    def status(self) -> dict:
        pulse = self.log.current_pulse
        angle = self.log.current_angle
        st = {
            "version": self.version,
            "uptime_s": round(time.time() - self.started, 1),
            "camera": {
                "backend": self.hub.backend, "width": self.hub.width, "height": self.hub.height,
                "fps": round(self.hub.fps(), 1), "frames": self.hub.seq, "clients": self.clients,
            },
            "servo": {
                "gpio": self.gpio, "pulse": pulse if pulse is not None else None,
                "angle": round(angle, 1) if angle is not None else None,
                "output_on": bool(pulse), "connected": bool(getattr(self.poller, "connected", False)),
                "backend": getattr(self.poller, "backend", "-"), "polls": getattr(self.poller, "polls", 0),
                "calib": self.log.spec.to_calib(),
            },
            "log": [e.text() for e in self.log.recent(20)],
        }
        if self.recorder is not None:
            st["record"] = self.recorder.status()
        if self.detector is not None:
            st["detect"] = self.detector.status()
        if self.camera is not None:
            st["settings"] = self.camera.describe()
        return st


def _make_handler(ctx: ServerContext):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):   # 콘솔을 조용히
            pass

        # ── 응답 도우미 ──
        def _send(self, code: int, ctype: str, body: bytes) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(body)

        def _json(self, code: int, obj) -> None:
            self._send(code, "application/json; charset=utf-8", json.dumps(obj, ensure_ascii=False).encode("utf-8"))

        def _error(self, code: int, msg: str) -> None:
            self._json(code, {"error": msg})

        def _query(self) -> dict:
            q = parse_qs(urlsplit(self.path).query, keep_blank_values=True)
            return {k: v[-1] for k, v in q.items()}

        def _body_json(self) -> dict:
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n > 0 else b""
            data = dict(self._query())
            if raw.strip():
                try:
                    obj = json.loads(raw.decode("utf-8"))
                except ValueError:
                    raise ApiError("본문이 JSON 이 아닙니다", 400)
                if not isinstance(obj, dict):
                    raise ApiError("본문은 JSON 객체여야 합니다", 400)
                data.update(obj)
            return data

        def _need(self, obj, what: str):
            if obj is None:
                raise ApiError(f"이 서버에는 {what} 기능이 없습니다", 501)
            return obj

        # ── GET ──
        def do_GET(self):
            path = urlsplit(self.path).path
            try:
                if path in ("/", "/index.html"):
                    html = (f"<!doctype html><title>pi_display v{ctx.version}</title>"
                            f"<body style='margin:0;background:#111;color:#ddd;font-family:sans-serif'>"
                            f"<img src='/stream.mjpg' style='max-width:100%'>"
                            f"<p style='padding:8px'>pi_display v{ctx.version} · <a href='/status.json' style='color:#9cf'>status.json</a> · "
                            f"<a href='/log.txt' style='color:#9cf'>log.txt</a> · <a href='/recordings.json' style='color:#9cf'>recordings.json</a></p></body>")
                    self._send(200, "text/html; charset=utf-8", html.encode("utf-8"))
                elif path == "/status.json":
                    self._json(200, ctx.status())
                elif path == "/log.txt":
                    body = "\n".join(e.text() for e in ctx.log.recent(500)) + "\n"
                    self._send(200, "text/plain; charset=utf-8", body.encode("utf-8"))
                elif path == "/snapshot.jpg":
                    jpeg, _ = ctx.hub.latest()
                    if jpeg is None:
                        self._send(503, "text/plain; charset=utf-8", "아직 프레임이 없습니다".encode("utf-8"))
                    else:
                        self._send(200, "image/jpeg", jpeg)
                elif path == "/stream.mjpg":
                    self._stream()
                elif path == "/record/status":
                    self._json(200, self._need(ctx.recorder, "녹화").status())
                elif path == "/recordings.json":
                    self._json(200, self._need(ctx.recorder, "녹화").list())
                elif path.startswith("/recordings/"):
                    self._send_file(self._need(ctx.recorder, "녹화").path_of(unquote(path[len("/recordings/"):])))
                elif path == "/camera/settings":
                    self._json(200, self._need(ctx.camera, "카메라 설정").describe())
                elif path == "/detect/status":
                    self._json(200, self._need(ctx.detector, "인식").status())
                elif path == "/servo/calib":
                    self._json(200, {"calib": ctx.log.spec.to_calib(), "text": str(ctx.log.spec)})
                else:
                    self._error(404, "not found")
            except ApiError as ex:
                self._error(ex.code, str(ex))
            except (BrokenPipeError, ConnectionResetError):
                pass

        # ── POST ──
        def do_POST(self):
            path = urlsplit(self.path).path
            try:
                data = self._body_json()
                if path == "/record/start":
                    rec = self._need(ctx.recorder, "녹화")
                    self._json(200, rec.start(data.get("name") or None))
                elif path == "/record/stop":
                    self._json(200, self._need(ctx.recorder, "녹화").stop())
                elif path == "/snapshot/save":
                    self._json(200, self._need(ctx.recorder, "녹화").snapshot())
                elif path == "/camera/settings":
                    self._json(200, self._need(ctx.camera, "카메라 설정").apply(data))
                elif path == "/detect/on":
                    det = self._need(ctx.detector, "인식")
                    det.set_enabled(True)
                    self._json(200, det.status())
                elif path == "/detect/off":
                    det = self._need(ctx.detector, "인식")
                    det.set_enabled(False)
                    self._json(200, det.status())
                elif path == "/detect/config":
                    det = self._need(ctx.detector, "인식")
                    try:
                        det.configure(every=int(data["every"]) if "every" in data else None,
                                      conf=float(data["conf"]) if "conf" in data else None)
                    except (TypeError, ValueError):
                        raise ApiError("every 는 정수, conf 는 0~1 실수여야 합니다", 400)
                    self._json(200, det.status())
                elif path == "/servo/calib":
                    if "calib" not in data:
                        raise ApiError("calib 값이 없습니다 (예 \"600,1500,2400,-80,80\")", 400)
                    self._json(200, ctx.set_calib(str(data["calib"])))
                else:
                    self._error(404, "not found")
            except ApiError as ex:
                self._error(ex.code, str(ex))
            except (BrokenPipeError, ConnectionResetError):
                pass

        def _send_file(self, path: str) -> None:
            size = os.path.getsize(path)
            name = os.path.basename(path)
            ctype = "image/jpeg" if name.lower().endswith(".jpg") else (
                "video/mp4" if name.lower().endswith(".mp4") else "application/octet-stream")
            self.send_response(200)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(size))
            self.send_header("Content-Disposition", f"attachment; filename=\"{name}\"")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            with open(path, "rb") as f:
                while True:
                    chunk = f.read(256 * 1024)
                    if not chunk:
                        break
                    self.wfile.write(chunk)

        def _stream(self) -> None:
            self.send_response(200)
            self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=frame")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            ctx.add_client(1)
            seq = 0
            try:
                while True:
                    jpeg, seq2 = ctx.hub.wait_next(seq, timeout=2.0)
                    if jpeg is None:
                        continue        # 카메라가 잠시 멈춤 — 연결은 유지
                    seq = seq2
                    self.wfile.write(b"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: "
                                     + str(len(jpeg)).encode() + b"\r\nX-Timestamp: "
                                     + f"{time.time():.3f}".encode() + b"\r\n\r\n")
                    self.wfile.write(jpeg)
                    self.wfile.write(b"\r\n")
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, OSError):
                pass
            finally:
                ctx.add_client(-1)

    return Handler


class MjpegServer:
    def __init__(self, ctx: ServerContext, bind: str = "0.0.0.0", port: int = 8889):
        self.ctx = ctx
        self.httpd = ThreadingHTTPServer((bind, port), _make_handler(ctx))
        self.httpd.daemon_threads = True
        self._thread: Optional[threading.Thread] = None

    @property
    def port(self) -> int:
        return self.httpd.server_address[1]

    def start(self) -> None:
        self._thread = threading.Thread(target=self.httpd.serve_forever, name="mjpeg-http", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self.httpd.shutdown()
        self.httpd.server_close()
