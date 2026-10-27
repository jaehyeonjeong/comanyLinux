"""카메라 백엔드 공통 뼈대. 구현은 start() 에서 캡처를 시작하고 JPEG 가 생길 때마다 hub.publish(jpeg, w, h) 한다.

녹화·사진·이미지 설정은 선택 기능이다 — picamera2 백엔드만 진짜로 구현하고, 나머지는 기본값(미리보기 JPEG 로 대신)을 쓴다.
    record_capable   "h264"(카메라 main 스트림을 하드웨어 인코더로) | "mjpeg-avi"(미리보기 JPEG 를 AVI 로)
    camera           picamera2 객체 (Recorder 가 start_encoder 에 쓴다)
    set_controls     밝기·대비·채도·AWB 즉시 적용 → 적용했으면 True
    capture_still    정지 사진을 path 에 저장하고 (w, h) 를 돌려줌. 못 하면 NotImplementedError → Recorder 가 미리보기 한 장을 저장
"""
import threading
from typing import Callable, Optional, Tuple

from ..frame_hub import FrameHub


class CameraBackend:
    name = "base"
    record_capable = "mjpeg-avi"
    supports_controls = False
    camera = None
    detail = ""

    def __init__(self, hub: FrameHub, width: int = 640, height: int = 480, fps: int = 10, quality: int = 70,
                 overlay: Optional[Callable[[], str]] = None, device: str = "",
                 record_mode: str = "1080p30", hflip: bool = False, vflip: bool = False, controls: Optional[dict] = None):
        self.hub = hub
        self.width, self.height, self.fps, self.quality = width, height, fps, quality
        self.overlay = overlay          # fake 백엔드가 그림에 적을 글(현재 각도 등)을 돌려주는 함수
        self.device = device
        self.record_mode = record_mode  # 녹화 모드 이름 (camera_settings.RECORD_MODES 의 키)
        self.hflip, self.vflip = hflip, vflip
        self.controls = dict(controls or {})
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None

    def start(self) -> None:
        raise NotImplementedError

    def stop(self) -> None:
        self._stop.set()
        t = self._thread
        if t is not None and t.is_alive():
            t.join(timeout=2.0)

    # ── 선택 기능 (기본: 없음) ──
    def set_controls(self, controls: dict) -> bool:
        self.controls.update(controls or {})
        return False

    def capture_still(self, path: str, allow_switch: bool = True) -> Tuple[int, int]:
        raise NotImplementedError

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
