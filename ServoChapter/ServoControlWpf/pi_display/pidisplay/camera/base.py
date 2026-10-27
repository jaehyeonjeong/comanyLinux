"""카메라 백엔드 공통 뼈대. 구현은 start() 에서 캡처를 시작하고 JPEG 가 생길 때마다 hub.publish(jpeg, w, h) 한다."""
import threading
from typing import Callable, Optional

from ..frame_hub import FrameHub


class CameraBackend:
    name = "base"

    def __init__(self, hub: FrameHub, width: int = 640, height: int = 480, fps: int = 10, quality: int = 70,
                 overlay: Optional[Callable[[], str]] = None, device: str = ""):
        self.hub = hub
        self.width, self.height, self.fps, self.quality = width, height, fps, quality
        self.overlay = overlay          # fake 백엔드가 그림에 적을 글(현재 각도 등)을 돌려주는 함수
        self.device = device
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None

    def start(self) -> None:
        raise NotImplementedError

    def stop(self) -> None:
        self._stop.set()
        t = self._thread
        if t is not None and t.is_alive():
            t.join(timeout=2.0)

    def _publish(self, jpeg: bytes) -> None:
        self.hub.backend = self.name
        self.hub.publish(jpeg, self.width, self.height)

    def _run_in_thread(self, target) -> None:
        self._thread = threading.Thread(target=target, name=f"cam-{self.name}", daemon=True)
        self._thread.start()


def split_mjpeg(buf: bytearray):
    """연속 바이트에서 완전한 JPEG(FFD8…FFD9)를 하나씩 꺼낸다. 꺼낸 만큼 buf 에서 지운다."""
    while True:
        start = buf.find(b"\xff\xd8")
        if start < 0:
            del buf[:]
            return
        end = buf.find(b"\xff\xd9", start + 2)
        if end < 0:
            if start > 0:
                del buf[:start]
            return
        yield bytes(buf[start:end + 2])
        del buf[:end + 2]
