"""libcamera-apps 의 명령줄 도구를 자식 프로세스로 띄워 MJPEG 를 stdout 으로 받는다 (python3-picamera2 가 없을 때)."""
import shutil
import subprocess

from .base import CameraBackend, split_mjpeg


class LibcameraVidBackend(CameraBackend):
    name = "libcamera-vid"

    def start(self) -> None:
        exe = shutil.which("libcamera-vid") or shutil.which("rpicam-vid")
        if not exe:
            raise FileNotFoundError("libcamera-vid / rpicam-vid 명령이 없습니다 (apt install libcamera-apps)")
        self._proc = subprocess.Popen(
            [exe, "-t", "0", "-n", "--codec", "mjpeg", "--width", str(self.width), "--height", str(self.height),
             "--framerate", str(self.fps), "--quality", str(self.quality), "-o", "-"],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
        # 1초 안에 죽으면 카메라가 없는 것
        try:
            rc = self._proc.wait(timeout=1.0)
            raise RuntimeError(f"{exe} 가 바로 종료됨 (코드 {rc}) — 카메라 연결/스택을 확인")
        except subprocess.TimeoutExpired:
            pass
        self._run_in_thread(self._loop)

    def _loop(self) -> None:
        buf = bytearray()
        out = self._proc.stdout
        while not self._stop.is_set():
            chunk = out.read(65536)
            if not chunk:
                break
            buf += chunk
            for jpeg in split_mjpeg(buf):
                self._publish(jpeg)

    def stop(self) -> None:
        super().stop()
        try:
            self._proc.terminate()
            self._proc.wait(timeout=2)
        except Exception:
            pass
