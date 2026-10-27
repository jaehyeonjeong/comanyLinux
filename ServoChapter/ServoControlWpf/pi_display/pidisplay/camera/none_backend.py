"""카메라가 없을 때의 자리 표시 백엔드.

실제 Pi 에 카메라 모듈이 아직 안 붙어 있어도(또는 카메라 스택이 깨져 있어도) pi_display 는 떠 있어야 한다 —
서보 이동 로그·pigpiod 상태·HTTP status.json·보정값 수신은 카메라와 무관하게 쓸모가 있기 때문이다.
그래서 'auto' 가 실제 백엔드를 전부 실패하면 이 백엔드로 내려와 1 fps 로 "NO CAMERA" 그림을 내보낸다.
녹화·사진은 Recorder 가 503 으로 거절한다(record_capable == "none").
카메라를 붙인 뒤에는 pi_display 를 다시 시작해야 한다(배포 스크립트가 그렇게 한다).
"""
import io
import time

from .base import CameraBackend
from .fake_backend import _JPEG_A


class NoCameraBackend(CameraBackend):
    name = "none"
    record_capable = "none"

    def start(self) -> None:
        try:
            from PIL import Image, ImageDraw  # noqa: F401
            self._pil = True
        except ImportError:
            self._pil = False
            self.width, self.height = 64, 48
        self._run_in_thread(self._loop)

    def _loop(self) -> None:
        while not self._stop.is_set():
            self._publish(self._draw() if self._pil else _JPEG_A)
            self._stop.wait(1.0)          # 1 fps — 화면·스트림이 "살아 있음" 만 보이면 된다

    def _draw(self) -> bytes:
        from PIL import Image, ImageDraw
        w, h = self.width, self.height
        img = Image.new("RGB", (w, h), (24, 24, 32))
        d = ImageDraw.Draw(img)
        d.rectangle([8, 8, w - 8, h - 8], outline=(120, 120, 140))
        # PIL 기본 글꼴은 한글이 없어 영문으로 적는다 (Pi 화면 상태 줄에는 한글 "카메라 없음" 이 따로 나온다)
        d.text((24, h // 2 - 30), "NO CAMERA", fill=(255, 200, 40))
        d.text((24, h // 2 - 10), "connect the camera module, then restart pi_display", fill=(200, 200, 200))
        d.text((24, h // 2 + 10), f"servo log / status.json still running   {time.strftime('%H:%M:%S')}", fill=(160, 160, 170))
        if self.overlay:
            try:
                d.text((24, h - 28), self.overlay(), fill=(255, 255, 255))
            except Exception:
                pass
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=self.quality)
        return buf.getvalue()
