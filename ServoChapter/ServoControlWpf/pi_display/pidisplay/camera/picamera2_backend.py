"""Pi 카메라 모듈 — libcamera 스택 (python3-picamera2). Bullseye 후기 이미지와 Bookworm 의 기본.

하드웨어 MJPEG 인코더(MJPEGEncoder)가 만든 JPEG 를 FileOutput 을 통해 받는다. 초기 버전부터 있던 공개 API 만 사용.
"""
import io

from .base import CameraBackend


class _HubOutput(io.BufferedIOBase):
    def __init__(self, backend: "Picamera2Backend"):
        super().__init__()
        self._b = backend

    def writable(self) -> bool:
        return True

    def write(self, buf) -> int:
        data = bytes(buf)
        if data.startswith(b"\xff\xd8"):
            self._b._publish(data)
        return len(data)


class Picamera2Backend(CameraBackend):
    name = "picamera2"

    def start(self) -> None:
        from picamera2 import Picamera2                     # ImportError → 다음 백엔드
        from picamera2.encoders import MJPEGEncoder
        from picamera2.outputs import FileOutput

        self._cam = Picamera2()
        cfg = self._cam.create_video_configuration(
            main={"size": (self.width, self.height)},
            controls={"FrameRate": float(self.fps)},
        )
        self._cam.configure(cfg)
        self._out = FileOutput(_HubOutput(self))
        self._cam.start_recording(MJPEGEncoder(), self._out)

    def stop(self) -> None:
        super().stop()
        try:
            self._cam.stop_recording()
            self._cam.close()
        except Exception:
            pass
