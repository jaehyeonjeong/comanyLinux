"""레거시 카메라 스택 (start_x=1, python3-picamera). 오래된 Bullseye/Buster 설정에서만."""
import io

from .base import CameraBackend


class PicameraLegacyBackend(CameraBackend):
    name = "picamera"

    def start(self) -> None:
        import picamera                                     # ImportError → 다음 백엔드
        self._cam = picamera.PiCamera(resolution=(self.width, self.height), framerate=self.fps)
        self._run_in_thread(self._loop)

    def _loop(self) -> None:
        stream = io.BytesIO()
        try:
            for _ in self._cam.capture_continuous(stream, "jpeg", use_video_port=True, quality=self.quality):
                self._publish(stream.getvalue())
                stream.seek(0)
                stream.truncate()
                if self._stop.is_set():
                    break
        except Exception:
            pass

    def stop(self) -> None:
        super().stop()
        try:
            self._cam.close()
        except Exception:
            pass
