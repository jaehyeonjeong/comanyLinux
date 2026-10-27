"""V4L2 장치(/dev/video0 — USB 웹캠 등)를 OpenCV 로 읽어 JPEG 로 인코딩 (python3-opencv)."""
import time

from .base import CameraBackend


class OpenCvBackend(CameraBackend):
    name = "opencv"

    def start(self) -> None:
        import cv2                                          # ImportError → 다음 백엔드
        self._cv2 = cv2
        index = int(self.device) if self.device.isdigit() else 0
        self._cap = cv2.VideoCapture(index)
        if not self._cap.isOpened():
            raise RuntimeError(f"/dev/video{index} 를 열 수 없습니다")
        self._cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
        self._cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
        ok, _ = self._cap.read()
        if not ok:
            self._cap.release()
            raise RuntimeError("카메라에서 프레임을 읽지 못했습니다")
        self._run_in_thread(self._loop)

    def _loop(self) -> None:
        cv2 = self._cv2
        period = 1.0 / max(1, self.fps)
        while not self._stop.is_set():
            t0 = time.time()
            ok, frame = self._cap.read()
            if not ok:
                self._stop.wait(0.2)
                continue
            ok, enc = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), self.quality])
            if ok:
                self.width, self.height = frame.shape[1], frame.shape[0]
                self._publish(enc.tobytes())
            self._stop.wait(max(0.0, period - (time.time() - t0)))

    def stop(self) -> None:
        super().stop()
        try:
            self._cap.release()
        except Exception:
            pass
