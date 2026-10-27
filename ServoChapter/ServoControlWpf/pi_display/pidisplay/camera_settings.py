"""카메라 설정(녹화 모드·반전·밝기·대비·채도·화이트밸런스)과, 설정을 바꿀 때 카메라를 다시 여는 관리자.

OV5647 (5 MP) 가 낼 수 있는 비디오 모드 세 가지를 '녹화 모드' 로 고른다. 미리보기(640×480, 10 fps)는 모드와 상관없이 같다.
    1080p30  1920×1080 30 fps   기본. Pi 4 하드웨어 H.264 인코더의 상한.
    720p60   1280×720  60 fps   빠른 팬 동작 관찰
    480p90   640×480   90 fps   더 빠른 동작 (화질 낮음)

즉시 적용되는 값: brightness(-1~1), contrast(0~2), saturation(0~2), awb.  → picamera2 set_controls
카메라를 다시 열어야 하는 값: record_mode, hflip, vflip.  → 녹화 중이면 거부(409)
설정은 ~/.config/pi_display/camera.json 에 저장되어 다음 실행에도 유지된다.
"""
import json
import os
import threading
from dataclasses import asdict, dataclass, replace
from typing import Callable, Dict, List, Optional, Tuple

from .errors import ApiError

RECORD_MODES: Dict[str, Tuple[int, int, int]] = {
    "1080p30": (1920, 1080, 30),
    "720p60": (1280, 720, 60),
    "480p90": (640, 480, 90),
}
AWB_MODES = ["auto", "daylight", "cloudy", "tungsten", "fluorescent", "indoor"]
# libcamera AwbModeEnum 값 (Auto=0, Incandescent=1, Tungsten=2, Fluorescent=3, Indoor=4, Daylight=5, Cloudy=6)
AWB_ENUM = {"auto": 0, "tungsten": 2, "fluorescent": 3, "indoor": 4, "daylight": 5, "cloudy": 6}
RESTART_FIELDS = ("record_mode", "hflip", "vflip")
STILL_SIZE = (2592, 1944)        # OV5647 최대 정지 해상도 (5 MP)


def default_settings_path() -> str:
    base = os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config"))
    return os.path.join(base, "pi_display", "camera.json")


def _clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


@dataclass(frozen=True)
class CameraSettings:
    record_mode: str = "1080p30"
    hflip: bool = False
    vflip: bool = False
    brightness: float = 0.0      # -1.0 ~ 1.0
    contrast: float = 1.0        # 0.0 ~ 2.0 (1 = 보통)
    saturation: float = 1.0      # 0.0 ~ 2.0
    awb: str = "auto"

    def to_dict(self) -> dict:
        return asdict(self)

    @staticmethod
    def from_dict(d: dict, base: Optional["CameraSettings"] = None) -> "CameraSettings":
        """부분 갱신: d 에 있는 키만 base 위에 덧씌운다. 값은 범위로 자르고, 모르는 모드/awb 는 거부."""
        s = base or CameraSettings()
        if not isinstance(d, dict):
            raise ApiError("설정은 JSON 객체여야 합니다", 400)
        kw = {}
        for k, v in d.items():
            if k == "record_mode":
                if v not in RECORD_MODES:
                    raise ApiError(f"녹화 모드는 {', '.join(RECORD_MODES)} 중 하나입니다", 400)
                kw[k] = v
            elif k in ("hflip", "vflip"):
                kw[k] = bool(v) if not isinstance(v, str) else v.lower() in ("1", "true", "on", "yes")
            elif k == "brightness":
                kw[k] = round(_clamp(float(v), -1.0, 1.0), 2)
            elif k in ("contrast", "saturation"):
                kw[k] = round(_clamp(float(v), 0.0, 2.0), 2)
            elif k == "awb":
                if v not in AWB_MODES:
                    raise ApiError(f"화이트밸런스는 {', '.join(AWB_MODES)} 중 하나입니다", 400)
                kw[k] = v
            # 모르는 키는 무시 (앞으로의 호환)
        return replace(s, **kw)

    def needs_restart(self, other: "CameraSettings") -> bool:
        return any(getattr(self, f) != getattr(other, f) for f in RESTART_FIELDS)

    def controls(self) -> dict:
        """picamera2 set_controls 에 넘길 값."""
        return {"Brightness": float(self.brightness), "Contrast": float(self.contrast),
                "Saturation": float(self.saturation), "AwbMode": AWB_ENUM.get(self.awb, 0)}

    def record_size(self) -> Tuple[int, int, int]:
        return RECORD_MODES[self.record_mode]

    # ── 파일 ──
    @staticmethod
    def load(path: Optional[str] = None) -> "CameraSettings":
        path = path or default_settings_path()
        try:
            with open(path, "r", encoding="utf-8") as f:
                return CameraSettings.from_dict(json.load(f))
        except (OSError, ValueError, ApiError):
            return CameraSettings()

    def save(self, path: Optional[str] = None) -> None:
        path = path or default_settings_path()
        try:
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            tmp = path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(self.to_dict(), f, ensure_ascii=False, indent=2)
            os.replace(tmp, path)
        except OSError:
            pass


class CameraManager:
    """백엔드를 열고(select_backend), 설정이 바뀌면 즉시 적용하거나 다시 연다."""

    def __init__(self, select, camera_name: str, backend_kw: dict, settings: CameraSettings,
                 settings_path: Optional[str] = None, is_busy: Optional[Callable[[], bool]] = None):
        self._select = select                 # select_backend(name, **kw) → (backend, tried)
        self.camera_name = camera_name
        self.backend_kw = dict(backend_kw)
        self.settings = settings
        self.settings_path = settings_path
        self._is_busy = is_busy or (lambda: False)
        self._lock = threading.Lock()
        self.backend = None
        self.tried: List[str] = []
        self.restarts = 0

    def _kw(self) -> dict:
        kw = dict(self.backend_kw)
        kw.update(record_mode=self.settings.record_mode, hflip=self.settings.hflip, vflip=self.settings.vflip,
                  controls=self.settings.controls())
        return kw

    def start(self) -> List[str]:
        with self._lock:
            self.backend, self.tried = self._select(self.camera_name, **self._kw())
            return self.tried

    def stop(self) -> None:
        with self._lock:
            if self.backend is not None:
                try:
                    self.backend.stop()
                finally:
                    self.backend = None

    def apply(self, partial: dict) -> dict:
        """설정 일부를 바꾼다. 돌려주는 값: {"settings": …, "restarted": bool, "applied": bool}"""
        new = CameraSettings.from_dict(partial, self.settings)
        with self._lock:
            restart = new.needs_restart(self.settings)
            if restart and self._is_busy():
                raise ApiError("녹화 중에는 녹화 모드·반전을 바꿀 수 없습니다. 먼저 녹화를 멈추세요.", 409)
            self.settings = new
            new.save(self.settings_path)
            applied = False
            if restart and self.backend is not None:
                try:
                    self.backend.stop()
                except Exception:
                    pass
                self.backend, self.tried = self._select(self.camera_name, **self._kw())
                self.restarts += 1
                applied = True
            elif self.backend is not None:
                fn = getattr(self.backend, "set_controls", None)
                if fn is not None:
                    try:
                        applied = bool(fn(new.controls()))
                    except Exception:
                        applied = False
        return {"settings": new.to_dict(), "restarted": restart, "applied": applied}

    def describe(self) -> dict:
        b = self.backend
        w, h, fps = self.settings.record_size()
        return {
            "settings": self.settings.to_dict(),
            "record_modes": list(RECORD_MODES),
            "awb_modes": AWB_MODES,
            "record_size": [w, h], "record_fps": fps,
            "still_size": list(STILL_SIZE),
            "record_capable": getattr(b, "record_capable", "mjpeg-avi") if b is not None else "-",
            "controls_supported": bool(getattr(b, "supports_controls", False)) if b is not None else False,
            "backend_detail": getattr(b, "detail", "") if b is not None else "",
            "restarts": self.restarts,
        }
