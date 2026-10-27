"""녹화기 — WPF 의 [● 녹화]/[■ 정지] 요청을 받아 파일로 남긴다.

두 가지 길:
    h264      picamera2 백엔드일 때. 카메라의 main 스트림(1080p30 등)을 Pi 4 **하드웨어 H.264 인코더**로 바로 파일에 쓴다.
              ffmpeg 가 있으면 .mp4 로 담고, 없으면 .h264 원시 스트림(VLC 재생 가능, `ffmpeg -i a.h264 -c copy a.mp4` 로 변환).
    mjpeg-avi 그 밖의 백엔드(libcamera-vid·legacy·opencv·fake). 미리보기용 JPEG 를 그대로 AVI(MJPG) 에 이어 붙인다 (CPU 거의 안 씀).
              WSL 에서 가짜 카메라로 전체 흐름을 시험하는 길이기도 하다.

규칙:
    - 쓰는 동안은 `이름.part` 로 열고 정지할 때 최종 이름으로 바꾼다 → 전송 스크립트는 `*.part` 를 건너뛰면 안전.
    - 파일 이름 rec_YYYYmmdd_HHMMSS.{mp4|h264|avi}, 사진은 img_YYYYmmdd_HHMMSS.jpg (같은 폴더, 같이 전송됨).
    - 시작 전 디스크 가드: 폴더 합이 max_gb 를 넘으면 가장 오래된 것부터 지움, 여유 공간이 min_free_mb 미만이면 거부(507).
    - max_minutes 가 지나면 스스로 멈춘다 (깜빡 잊고 켜 둔 녹화가 SD 카드를 다 채우지 않게).
"""
import os
import shutil
import threading
import time
from typing import Callable, List, Optional

from .errors import ApiError
from .avi_writer import AviMjpegWriter
from .frame_hub import FrameHub

RECORD_EXTS = (".mp4", ".h264", ".avi")
IMAGE_EXTS = (".jpg",)
ALL_EXTS = RECORD_EXTS + IMAGE_EXTS
DEFAULT_REC_DIR = os.path.expanduser("~/Videos/pi_display")

# 녹화 모드별 기본 비트레이트 (bit/s): 1080p30 → 8 Mbps, 720p60 → 6, 480p90 → 4
DEFAULT_BITRATES = {"1080p30": 8_000_000, "720p60": 6_000_000, "480p90": 4_000_000}


def safe_name(name: str) -> str:
    """경로 탈출 금지 + 허용 확장자만."""
    base = os.path.basename(name or "")
    if not base or base in (".", "..") or not base.lower().endswith(ALL_EXTS):
        raise ApiError("잘못된 파일 이름입니다", 400)
    return base


def human_size(n: int) -> str:
    if n >= 1024 * 1024 * 1024:
        return f"{n / 1024 / 1024 / 1024:.2f} GB"
    if n >= 1024 * 1024:
        return f"{n / 1024 / 1024:.1f} MB"
    return f"{n / 1024:.0f} KB"


def hms(seconds: float) -> str:
    s = int(max(0, seconds))
    return f"{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}"


class Recorder:
    def __init__(self, hub: FrameHub, backend, rec_dir: str = DEFAULT_REC_DIR, max_minutes: float = 60,
                 max_gb: float = 8.0, min_free_mb: int = 500, bitrate: Optional[int] = None,
                 on_event: Optional[Callable[[str, str], None]] = None, clock=time.time):
        self.hub = hub
        self.backend = backend                  # record_capable / camera / record_mode 를 본다 (바뀔 수 있으니 매번 getattr)
        self.rec_dir = rec_dir
        self.max_minutes = max_minutes
        self.max_gb = max_gb
        self.min_free_mb = min_free_mb
        self.bitrate = bitrate
        self.on_event = on_event                # (kind, text) → MovementLog 등
        self._clock = clock
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._writer: Optional[AviMjpegWriter] = None
        self._encoder = None                    # picamera2 H264Encoder
        self._part: Optional[str] = None
        self._final: Optional[str] = None
        self._kind = ""
        self._started = 0.0
        self._frames = 0
        self.last_result: Optional[dict] = None
        os.makedirs(rec_dir, exist_ok=True)

    # ── 상태 ──
    @property
    def recording(self) -> bool:
        return self._part is not None

    def _kind_for_backend(self) -> str:
        cap = getattr(self.backend, "record_capable", "mjpeg-avi")
        if cap == "none":
            return "none"                       # 카메라 없음(자리 표시 백엔드) — 녹화·사진 거절
        return "h264" if cap == "h264" else "mjpeg-avi"

    def _require_camera(self) -> None:
        if self._kind_for_backend() == "none":
            raise ApiError("카메라가 없습니다 — 카메라 모듈을 연결한 뒤 pi_display 를 다시 시작하세요", 503)

    def _bytes_now(self) -> int:
        p = self._part
        if not p:
            return 0
        try:
            return os.path.getsize(p)
        except OSError:
            return 0

    def status(self) -> dict:
        with self._lock:
            rec = self.recording
            st = {
                "recording": rec,
                "kind": self._kind if rec else self._kind_for_backend(),
                "file": os.path.basename(self._final) if rec and self._final else None,
                "seconds": round(self._clock() - self._started, 1) if rec else 0.0,
                "bytes": self._bytes_now() if rec else 0,
                "frames": self._frames if rec else 0,
                "dir": self.rec_dir,
                "max_minutes": self.max_minutes,
                "last": self.last_result,
            }
        st["disk"] = self.disk()
        return st

    def disk(self) -> dict:
        try:
            du = shutil.disk_usage(self.rec_dir)
            free_mb = du.free // (1024 * 1024)
        except OSError:
            free_mb = -1
        return {"free_mb": free_mb, "used_mb": self._dir_bytes() // (1024 * 1024), "max_gb": self.max_gb}

    # ── 목록 ──
    def _files(self) -> List[str]:
        try:
            names = os.listdir(self.rec_dir)
        except OSError:
            return []
        return [n for n in names if n.lower().endswith(ALL_EXTS) and not n.endswith(".part")]

    def _dir_bytes(self) -> int:
        total = 0
        for n in self._files():
            try:
                total += os.path.getsize(os.path.join(self.rec_dir, n))
            except OSError:
                pass
        return total

    def list(self) -> List[dict]:
        out = []
        for n in self._files():
            p = os.path.join(self.rec_dir, n)
            try:
                st = os.stat(p)
            except OSError:
                continue
            out.append({"name": n, "bytes": st.st_size, "mtime": int(st.st_mtime),
                        "kind": "image" if n.lower().endswith(IMAGE_EXTS) else "video"})
        out.sort(key=lambda d: d["mtime"], reverse=True)
        return out

    def path_of(self, name: str) -> str:
        p = os.path.join(self.rec_dir, safe_name(name))
        if not os.path.isfile(p):
            raise ApiError("그런 파일이 없습니다", 404)
        return p

    # ── 디스크 가드 ──
    def _guard(self) -> List[str]:
        removed: List[str] = []
        limit = int(self.max_gb * 1024 * 1024 * 1024)
        files = sorted(self._files(), key=lambda n: os.path.getmtime(os.path.join(self.rec_dir, n)))
        while files and self._dir_bytes() > limit:
            victim = files.pop(0)
            try:
                os.remove(os.path.join(self.rec_dir, victim))
                removed.append(victim)
            except OSError:
                break
        if removed:
            self._emit("rec", f"디스크 정리: {len(removed)}개 삭제 ({', '.join(removed[:3])}{'…' if len(removed) > 3 else ''})")
        free = self.disk()["free_mb"]
        if 0 <= free < self.min_free_mb:
            raise ApiError(f"디스크 여유 공간이 {free} MB 뿐입니다 (최소 {self.min_free_mb} MB)", 507)
        return removed

    def _emit(self, kind: str, text: str) -> None:
        if self.on_event:
            try:
                self.on_event(kind, text)
            except Exception:
                pass

    # ── 시작 / 정지 ──
    def start(self, name: Optional[str] = None) -> dict:
        with self._lock:
            if self.recording:
                raise ApiError("이미 녹화 중입니다", 409)
            self._require_camera()
            if self.hub.latest()[0] is None:
                raise ApiError("카메라 프레임이 아직 없습니다", 503)
            self._guard()
            kind = self._kind_for_backend()
            stamp = time.strftime("%Y%m%d_%H%M%S", time.localtime(self._clock()))
            base = (name or f"rec_{stamp}").strip()
            base = os.path.basename(base) or f"rec_{stamp}"
            base = os.path.splitext(base)[0]
            if kind == "h264":
                ext = ".mp4" if shutil.which("ffmpeg") else ".h264"
            else:
                ext = ".avi"
            final = os.path.join(self.rec_dir, base + ext)
            if os.path.exists(final):
                final = os.path.join(self.rec_dir, f"{base}_{int(self._clock()) % 1000:03d}{ext}")
            part = final + ".part"
            self._started = self._clock()
            self._frames = 0
            self._kind = kind
            try:
                if kind == "h264":
                    self._start_h264(part, ext)
                else:
                    self._start_avi(part)
            except Exception as ex:
                self._cleanup_failed(part)
                raise ApiError(f"녹화를 시작할 수 없습니다: {ex}", 500)
            self._part, self._final = part, final
            self._stop.clear()
            self._thread = threading.Thread(target=self._loop, name="recorder", daemon=True)
            self._thread.start()
        self._emit("rec", f"● 녹화 시작  {os.path.basename(final)}  ({kind})")
        return self.status()

    def _cleanup_failed(self, part: str) -> None:
        self._writer = None
        self._encoder = None
        try:
            if os.path.exists(part):
                os.remove(part)
        except OSError:
            pass

    def _start_avi(self, part: str) -> None:
        w, h = self.hub.width or 640, self.hub.height or 480
        fps = getattr(self.backend, "fps", 10) or 10
        self._writer = AviMjpegWriter(part, w, h, fps, clock=self._clock)

    def _start_h264(self, part: str, ext: str) -> None:
        from picamera2.encoders import H264Encoder          # type: ignore
        from picamera2.outputs import FileOutput            # type: ignore
        cam = self.backend.camera
        mode = getattr(self.backend, "record_mode", "1080p30")
        bitrate = self.bitrate or DEFAULT_BITRATES.get(mode, 6_000_000)
        enc = H264Encoder(bitrate=bitrate)
        if ext == ".mp4":
            from picamera2.outputs import FfmpegOutput      # type: ignore
            out = FfmpegOutput(part)
        else:
            out = FileOutput(part)
        cam.start_encoder(enc, out, name="main")
        self._encoder = enc

    def _loop(self) -> None:
        """mjpeg-avi: 허브의 새 프레임을 받아 적는다. h264: 시간 제한만 감시한다."""
        seq = self.hub.seq
        limit = self.max_minutes * 60 if self.max_minutes and self.max_minutes > 0 else None
        while not self._stop.is_set():
            if limit is not None and self._clock() - self._started >= limit:
                threading.Thread(target=self.stop, kwargs={"reason": f"시간 제한 {self.max_minutes:g}분"}, daemon=True).start()
                return
            if self._writer is not None:
                jpeg, seq2 = self.hub.wait_next(seq, timeout=0.5)
                if jpeg is None:
                    continue
                seq = seq2
                with self._lock:
                    if self._writer is not None and not self._stop.is_set():
                        self._writer.add_frame(jpeg)
                        self._frames += 1
            else:
                self._stop.wait(0.5)

    def stop(self, reason: str = "") -> dict:
        with self._lock:
            if not self.recording:
                raise ApiError("녹화 중이 아닙니다", 409)
            self._stop.set()
            part, final, kind = self._part, self._final, self._kind
            seconds = self._clock() - self._started
            if self._writer is not None:
                try:
                    self._writer.close()
                finally:
                    self._writer = None
            if self._encoder is not None:
                try:
                    self.backend.camera.stop_encoder(self._encoder)
                except Exception:
                    pass
                self._encoder = None
            self._part = self._final = None
        t = self._thread
        if t is not None and t.is_alive() and t is not threading.current_thread():
            t.join(timeout=2.0)
        try:
            os.replace(part, final)
        except OSError:
            final = part
        try:
            size = os.path.getsize(final)
        except OSError:
            size = 0
        result = {"name": os.path.basename(final), "bytes": size, "seconds": round(seconds, 1),
                  "frames": self._frames, "kind": kind, "reason": reason}
        self.last_result = result
        self._emit("rec", f"■ 녹화 끝  {result['name']}  {human_size(size)}  {hms(seconds)}" + (f"  ({reason})" if reason else ""))
        return result

    # ── 사진 ──
    def snapshot(self) -> dict:
        self._require_camera()
        self._guard()
        stamp = time.strftime("%Y%m%d_%H%M%S", time.localtime(self._clock()))
        path = os.path.join(self.rec_dir, f"img_{stamp}.jpg")
        if os.path.exists(path):
            path = os.path.join(self.rec_dir, f"img_{stamp}_{int(self._clock() * 1000) % 1000:03d}.jpg")
        note = ""
        size = None
        cap = getattr(self.backend, "capture_still", None)
        if cap is not None:
            try:
                size = cap(path, allow_switch=not self.recording)
                if self.recording:
                    note = "녹화 중이라 녹화 해상도로 저장"
            except NotImplementedError:
                size = None
            except Exception as ex:
                raise ApiError(f"사진을 찍을 수 없습니다: {ex}", 500)
        if size is None:
            jpeg, _ = self.hub.latest()
            if jpeg is None:
                raise ApiError("카메라 프레임이 아직 없습니다", 503)
            with open(path, "wb") as f:
                f.write(jpeg)
            size = (self.hub.width, self.hub.height)
            note = note or "이 카메라 백엔드는 미리보기 해상도로 저장"
        nbytes = os.path.getsize(path)
        self._emit("rec", f"📷 사진  {os.path.basename(path)}  {size[0]}×{size[1]}  {human_size(nbytes)}")
        return {"name": os.path.basename(path), "width": size[0], "height": size[1], "bytes": nbytes, "note": note}

    def close(self) -> None:
        if self.recording:
            try:
                self.stop(reason="프로그램 종료")
            except ApiError:
                pass
