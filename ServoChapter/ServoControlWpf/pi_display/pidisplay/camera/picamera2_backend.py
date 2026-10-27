"""Pi 카메라 모듈 — libcamera 스택 (python3-picamera2). Bullseye 후기 이미지와 Bookworm 의 기본.

두 스트림을 동시에 연다 (OV5647 + Pi 4 기준):
    main   녹화 모드 크기 (1920×1080@30 / 1280×720@60 / 640×480@90)  → Recorder 가 H264Encoder(하드웨어)로 파일에 쓴다
    lores  미리보기 (640×480)                                           → MJPEGEncoder(하드웨어) → FrameHub → HTTP/화면
카메라 프레임 속도는 녹화 모드를 따르므로(30/60/90 fps), 미리보기는 k 장마다 1장만 publish 해서 설정한 fps(기본 10)에 맞춘다.
lores 스트림을 MJPEGEncoder 가 받지 못하는 구버전이면 JpegEncoder(소프트웨어)로, 그것도 안 되면 main 하나만 쓰는 옛 방식으로 강등한다.

사진: 녹화 중이 아니면 switch_mode_and_capture_file 로 5 MP(2592×1944) 정지 설정으로 잠시 바꿔 한 장 찍는다 (스트림이 약 1초 끊김).
녹화 중이면 main 스트림의 현재 프레임을 저장한다.
"""
import io
import threading
from typing import Tuple

from .base import CameraBackend

RECORD_MODES = {"1080p30": ((1920, 1080), 30), "720p60": ((1280, 720), 60), "480p90": ((640, 480), 90)}
STILL_SIZE = (2592, 1944)


class _HubOutput(io.BufferedIOBase):
    def __init__(self, backend: "Picamera2Backend", keep_every: int):
        super().__init__()
        self._b = backend
        self._keep = max(1, keep_every)
        self._n = 0

    def writable(self) -> bool:
        return True

    def write(self, buf) -> int:
        data = bytes(buf)
        if data.startswith(b"\xff\xd8"):
            self._n += 1
            if self._n % self._keep == 0:
                self._b._publish(data)
        return len(data)


class Picamera2Backend(CameraBackend):
    name = "picamera2"
    record_capable = "h264"
    supports_controls = True

    def start(self) -> None:
        from picamera2 import Picamera2                     # ImportError → 다음 백엔드
        from picamera2.encoders import MJPEGEncoder
        from picamera2.outputs import FileOutput
        try:
            from libcamera import Transform
            transform = Transform(hflip=1 if self.hflip else 0, vflip=1 if self.vflip else 0)
        except Exception:
            transform = None

        rec_size, rec_fps = RECORD_MODES.get(self.record_mode, RECORD_MODES["1080p30"])
        self._lock = threading.Lock()
        self._cam = Picamera2()
        self.camera = self._cam
        controls = {"FrameRate": float(rec_fps)}
        controls.update(self.controls)
        keep = max(1, int(round(rec_fps / max(1, self.fps))))

        kw = {"controls": controls}
        if transform is not None:
            kw["transform"] = transform
        try:
            cfg = self._cam.create_video_configuration(
                main={"size": rec_size}, lores={"size": (self.width, self.height)}, **kw)
            self._cam.configure(cfg)
            self._out = FileOutput(_HubOutput(self, keep))
            self._preview_name = "lores"
            try:
                self._enc = MJPEGEncoder()
                self._cam.start_encoder(self._enc, self._out, name="lores")
                self.detail = f"main {rec_size[0]}×{rec_size[1]}@{rec_fps} + lores MJPEG(HW)"
            except Exception:
                from picamera2.encoders import JpegEncoder
                self._enc = JpegEncoder(q=self.quality)
                self._cam.start_encoder(self._enc, self._out, name="lores")
                self.detail = f"main {rec_size[0]}×{rec_size[1]}@{rec_fps} + lores JPEG(SW)"
            self._cam.start()
        except Exception as ex:
            # 구버전: 두 스트림 불가 → main 하나만, 녹화는 AVI 경로
            try:
                self._cam.stop()
            except Exception:
                pass
            cfg = self._cam.create_video_configuration(main={"size": (self.width, self.height)},
                                                       controls={"FrameRate": float(self.fps), **self.controls})
            self._cam.configure(cfg)
            self._out = FileOutput(_HubOutput(self, 1))
            self._enc = MJPEGEncoder()
            self._cam.start_recording(self._enc, self._out)
            self._preview_name = "main"
            self.record_capable = "mjpeg-avi"
            self.detail = f"단일 스트림 (dual-stream 실패: {type(ex).__name__}) — 녹화는 MJPEG AVI"

    def set_controls(self, controls: dict) -> bool:
        super().set_controls(controls)
        try:
            self._cam.set_controls(dict(controls))
            return True
        except Exception:
            return False

    def capture_still(self, path: str, allow_switch: bool = True) -> Tuple[int, int]:
        with self._lock:
            if allow_switch and self._preview_name == "lores":
                still = self._cam.create_still_configuration(main={"size": STILL_SIZE})
                self._cam.switch_mode_and_capture_file(still, path)
                return STILL_SIZE
            self._cam.capture_file(path, name="main")
            size = self._cam.camera_configuration()["main"]["size"]
            return int(size[0]), int(size[1])

    def stop(self) -> None:
        super().stop()
        try:
            if self._preview_name == "lores":
                self._cam.stop_encoder(self._enc)
                self._cam.stop()
            else:
                self._cam.stop_recording()
            self._cam.close()
        except Exception:
            pass
