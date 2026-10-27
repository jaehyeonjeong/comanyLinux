"""HTTP 서버 (표준 라이브러리만): WPF 와 브라우저가 Pi 카메라를 보고 상태를 읽는 창구.

    GET /stream.mjpg   multipart/x-mixed-replace; boundary=frame — 파트마다 Content-Type·Content-Length
    GET /snapshot.jpg  최신 한 장
    GET /status.json   version, camera{backend,width,height,fps,clients}, servo{gpio,pulse,angle,output_on,connected,backend}, log[최근 20]
    GET /log.txt       메모리에 있는 로그 전체 (텍스트)
    GET /              <img src=stream.mjpg> 한 줄짜리 확인용 페이지

스트림은 항상 FrameHub 의 "최신 1장" 만 보내므로 느린 클라이언트가 있어도 밀리지 않는다.
"""
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Optional

from .frame_hub import FrameHub
from .movement_log import MovementLog


class ServerContext:
    def __init__(self, hub: FrameHub, log: MovementLog, poller=None, version: str = "", gpio: int = 18):
        self.hub, self.log, self.poller, self.version, self.gpio = hub, log, poller, version, gpio
        self.clients = 0
        self.started = time.time()
        self._lock = threading.Lock()

    def add_client(self, n: int) -> None:
        with self._lock:
            self.clients += n

    def status(self) -> dict:
        pulse = self.log.current_pulse
        angle = self.log.current_angle
        return {
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
            },
            "log": [e.text() for e in self.log.recent(20)],
        }


def _make_handler(ctx: ServerContext):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *args):   # 콘솔을 조용히
            pass

        def _send(self, code: int, ctype: str, body: bytes) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?")[0]
            if path in ("/", "/index.html"):
                html = (f"<!doctype html><title>pi_display v{ctx.version}</title>"
                        f"<body style='margin:0;background:#111;color:#ddd;font-family:sans-serif'>"
                        f"<img src='/stream.mjpg' style='max-width:100%'>"
                        f"<p style='padding:8px'>pi_display v{ctx.version} · <a href='/status.json' style='color:#9cf'>status.json</a> · "
                        f"<a href='/log.txt' style='color:#9cf'>log.txt</a></p></body>")
                self._send(200, "text/html; charset=utf-8", html.encode("utf-8"))
            elif path == "/status.json":
                self._send(200, "application/json; charset=utf-8", json.dumps(ctx.status(), ensure_ascii=False).encode("utf-8"))
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
            else:
                self._send(404, "text/plain; charset=utf-8", b"not found")

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
