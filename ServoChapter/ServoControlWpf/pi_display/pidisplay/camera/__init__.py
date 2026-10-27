"""카메라 백엔드 선택. 'auto' 면 순서대로 시도해 처음 성공한 것을 쓴다 (자동 강등).

    picamera2     Bullseye/Bookworm 의 libcamera 스택 (python3-picamera2)
    libcamera-vid libcamera-apps 의 명령줄 도구를 자식 프로세스로 (MJPEG → stdout)
    picamera      레거시 카메라 스택 (start_x=1, python3-picamera)
    opencv        /dev/video0 (USB 웹캠, 또는 libcamera 의 v4l2 호환 노드)
    fake          움직이는 테스트 패턴 (PIL 있으면 그리기, 없으면 내장 JPEG 2장 번갈아) — Pi 없이 시험용
    none          카메라 없음 자리 표시 ("NO CAMERA" 1 fps). 'auto' 가 위 넷을 다 실패하면 여기로 내려와
                  프로그램은 계속 돈다 (서보 로그·status.json·보정 수신은 카메라와 무관). 녹화·사진은 503.
"""
from typing import List, Tuple

from .base import CameraBackend

ORDER = ["picamera2", "libcamera-vid", "picamera", "opencv"]
NAMES = ORDER + ["fake", "none"]
NO_CAMERA_HINT = "카메라 없음 → 자리 표시 화면으로 계속 실행 (카메라 모듈을 연결한 뒤 pi_display 를 다시 시작)"


def _make(name: str, **kw) -> CameraBackend:
    if name == "picamera2":
        from .picamera2_backend import Picamera2Backend
        return Picamera2Backend(**kw)
    if name == "libcamera-vid":
        from .libcamera_vid_backend import LibcameraVidBackend
        return LibcameraVidBackend(**kw)
    if name == "picamera":
        from .picamera_legacy_backend import PicameraLegacyBackend
        return PicameraLegacyBackend(**kw)
    if name == "opencv":
        from .opencv_backend import OpenCvBackend
        return OpenCvBackend(**kw)
    if name == "fake":
        from .fake_backend import FakeBackend
        return FakeBackend(**kw)
    if name == "none":
        from .none_backend import NoCameraBackend
        return NoCameraBackend(**kw)
    raise ValueError(f"알 수 없는 카메라 백엔드: {name}")


def select_backend(name: str, **kw) -> Tuple[CameraBackend, List[str]]:
    """백엔드를 만들고 start() 까지 해서 돌려준다. (backend, 시도 기록)

    'auto' 는 ORDER 를 차례로 시도하고, 전부 실패하면 예외 대신 'none'(자리 표시) 으로 내려온다 —
    실제 Pi 에 카메라가 아직 없어도 화면 프로그램이 종료되지 않도록. 이름을 지정했는데 실패하면 RuntimeError."""
    tried: List[str] = []
    names = ORDER if name == "auto" else [name]
    for n in names:
        try:
            b = _make(n, **kw)
            b.start()
            tried.append(f"{n}: 사용")
            return b, tried
        except Exception as ex:      # ImportError, 장치 없음, 권한 등 → 다음으로
            tried.append(f"{n}: {type(ex).__name__}: {str(ex).strip()[:120]}")
    if name == "auto":
        b = _make("none", **kw)
        b.start()
        b.detail = "카메라 없음"
        tried.append(f"none: {NO_CAMERA_HINT}")
        return b, tried
    raise RuntimeError("카메라를 열 수 없습니다:\n  " + "\n  ".join(tried) +
                       "\n  (--fake-camera 로 테스트 패턴을, --camera none 으로 자리 표시 화면을 쓸 수 있습니다)")
