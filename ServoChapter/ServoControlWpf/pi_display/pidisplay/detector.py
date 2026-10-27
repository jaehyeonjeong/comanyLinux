"""OpenCV 인체·사물 인식 — 미리보기 프레임에 박스와 이름을 그려 넣고, 결과를 status.json 에도 적는다.

엔진 우선순위:
    1. MobileNet-SSD (Caffe, VOC 20종: 사람·자동차·개·고양이·병·의자…)  — ~/.local/share/pi_display/models/ 에
       deploy.prototxt + mobilenet_iter_73000.caffemodel 이 있으면 (install_pi_deps.sh --models 가 내려받음)
    2. HOG 사람 검출기 (OpenCV 내장, 사람만, 느림)
    3. OpenCV 가 없으면 available=False (WSL) — API 는 살아 있고 "인식 불가" 만 알린다.

프레임 흐름: 카메라 → FrameHub.publish → **filter = Detector.annotate** → 보관 → HTTP/화면.
    annotate 는 every 장마다 원본 한 장을 작업 스레드에 넘기고(큐 깊이 1), 가장 최근 결과 박스를 현재 프레임에 그린다.
    추론은 작업 스레드에서 돌아 미리보기 fps 를 막지 않는다. 박스가 그려진 프레임은 미리보기·AVI 녹화에 들어가고,
    H.264 녹화(카메라 main 스트림)에는 들어가지 않는다 (원본 보존).
"""
import os
import threading
import time
from collections import deque
from typing import List, Optional

VOC_CLASSES = ["background", "aeroplane", "bicycle", "bird", "boat", "bottle", "bus", "car", "cat", "chair", "cow",
               "diningtable", "dog", "horse", "motorbike", "person", "pottedplant", "sheep", "sofa", "train", "tvmonitor"]
KO = {"aeroplane": "비행기", "bicycle": "자전거", "bird": "새", "boat": "배", "bottle": "병", "bus": "버스", "car": "자동차",
      "cat": "고양이", "chair": "의자", "cow": "소", "diningtable": "식탁", "dog": "개", "horse": "말", "motorbike": "오토바이",
      "person": "사람", "pottedplant": "화분", "sheep": "양", "sofa": "소파", "train": "기차", "tvmonitor": "모니터"}
DEFAULT_MODEL_DIR = os.path.join(os.environ.get("XDG_DATA_HOME", os.path.expanduser("~/.local/share")), "pi_display", "models")
PROTOTXT, CAFFEMODEL = "deploy.prototxt", "mobilenet_iter_73000.caffemodel"


class Detection:
    __slots__ = ("label", "conf", "box")

    def __init__(self, label: str, conf: float, box):
        self.label, self.conf, self.box = label, conf, tuple(box)   # box = (x, y, w, h) 0~1 비율

    def to_dict(self) -> dict:
        return {"label": self.label, "label_ko": KO.get(self.label, self.label), "conf": round(self.conf, 2),
                "box": [round(v, 3) for v in self.box]}


class OpenCvEngine:
    """cv2 가 있을 때. detect(jpeg) → [Detection], draw(jpeg, dets) → jpeg"""

    def __init__(self, model_dir: str, conf: float):
        import cv2  # noqa: F401  (ImportError → Detector 가 available=False 로)
        import numpy as np  # noqa: F401
        self.cv2, self.np = cv2, np
        self.conf = conf
        proto, model = os.path.join(model_dir, PROTOTXT), os.path.join(model_dir, CAFFEMODEL)
        if os.path.isfile(proto) and os.path.isfile(model):
            self.net = cv2.dnn.readNetFromCaffe(proto, model)
            self.name = "MobileNet-SSD"
            self.hog = None
        else:
            self.net = None
            self.hog = cv2.HOGDescriptor()
            self.hog.setSVMDetector(cv2.HOGDescriptor_getDefaultPeopleDetector())
            self.name = "HOG(사람만)"

    def _decode(self, jpeg: bytes):
        arr = self.np.frombuffer(jpeg, dtype=self.np.uint8)
        return self.cv2.imdecode(arr, self.cv2.IMREAD_COLOR)

    def detect(self, jpeg: bytes) -> List[Detection]:
        img = self._decode(jpeg)
        if img is None:
            return []
        h, w = img.shape[:2]
        out: List[Detection] = []
        if self.net is not None:
            blob = self.cv2.dnn.blobFromImage(self.cv2.resize(img, (300, 300)), 0.007843, (300, 300), 127.5)
            self.net.setInput(blob)
            det = self.net.forward()
            for i in range(det.shape[2]):
                c = float(det[0, 0, i, 2])
                if c < self.conf:
                    continue
                idx = int(det[0, 0, i, 1])
                x1, y1, x2, y2 = [float(v) for v in det[0, 0, i, 3:7]]
                x1, y1, x2, y2 = max(0.0, x1), max(0.0, y1), min(1.0, x2), min(1.0, y2)
                out.append(Detection(VOC_CLASSES[idx] if 0 <= idx < len(VOC_CLASSES) else str(idx), c, (x1, y1, x2 - x1, y2 - y1)))
        else:
            small = self.cv2.resize(img, (min(w, 400), int(h * min(w, 400) / w)))
            sh, sw = small.shape[:2]
            rects, weights = self.hog.detectMultiScale(small, winStride=(8, 8), padding=(8, 8), scale=1.05)
            for (x, y, bw, bh), wt in zip(rects, weights):
                if float(wt) < self.conf:
                    continue
                out.append(Detection("person", float(min(1.0, wt)), (x / sw, y / sh, bw / sw, bh / sh)))
        return out

    def draw(self, jpeg: bytes, dets: List[Detection], quality: int = 70) -> bytes:
        if not dets:
            return jpeg
        img = self._decode(jpeg)
        if img is None:
            return jpeg
        h, w = img.shape[:2]
        for d in dets:
            x, y, bw, bh = d.box
            p1 = (int(x * w), int(y * h))
            p2 = (int((x + bw) * w), int((y + bh) * h))
            color = (60, 220, 60) if d.label == "person" else (40, 170, 255)
            self.cv2.rectangle(img, p1, p2, color, 2)
            text = f"{d.label} {d.conf:.0%}"          # cv2 기본 글꼴은 한글이 없어 영문 라벨
            self.cv2.putText(img, text, (p1[0] + 3, max(12, p1[1] - 4)), self.cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)
        ok, buf = self.cv2.imencode(".jpg", img, [int(self.cv2.IMWRITE_JPEG_QUALITY), quality])
        return buf.tobytes() if ok else jpeg


class FakeEngine:
    """테스트용: 늘 같은 박스를 돌려주고, 그리기는 JPEG 끝에 표식을 붙이지 않고 그대로 둔다(횟수만 셈)."""
    name = "fake"

    def __init__(self, dets: Optional[List[Detection]] = None):
        self.dets = dets if dets is not None else [Detection("person", 0.9, (0.1, 0.1, 0.3, 0.6)),
                                                   Detection("chair", 0.7, (0.6, 0.5, 0.3, 0.4))]
        self.detect_calls = 0
        self.draw_calls = 0

    def detect(self, jpeg: bytes) -> List[Detection]:
        self.detect_calls += 1
        return list(self.dets)

    def draw(self, jpeg: bytes, dets: List[Detection], quality: int = 70) -> bytes:
        self.draw_calls += 1
        return jpeg


class Detector:
    def __init__(self, every: int = 3, conf: float = 0.5, model_dir: Optional[str] = None, engine=None,
                 enabled: bool = False, quality: int = 70):
        self.every = max(1, int(every))
        self.conf = float(conf)
        self.model_dir = model_dir or DEFAULT_MODEL_DIR
        self.quality = quality
        self.error = ""
        if engine is not None:
            self.engine = engine
        else:
            try:
                self.engine = OpenCvEngine(self.model_dir, self.conf)
            except ImportError as ex:
                self.engine = None
                self.error = f"OpenCV 없음 ({ex}) — apt install python3-opencv"
            except Exception as ex:          # 모델 파일 손상 등
                self.engine = None
                self.error = f"{type(ex).__name__}: {str(ex)[:120]}"
        self.available = self.engine is not None
        self.model = getattr(self.engine, "name", "-") if self.engine else "-"
        self._enabled = bool(enabled) and self.available
        self._lock = threading.Lock()
        self._pending: Optional[bytes] = None
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._n = 0
        self.latest: List[Detection] = []
        self.latest_t = 0.0
        self.infer_ms = 0.0
        self._times = deque(maxlen=20)
        self._thread: Optional[threading.Thread] = None
        if self.available:
            self._thread = threading.Thread(target=self._worker, name="detector", daemon=True)
            self._thread.start()

    # ── 제어 ──
    @property
    def enabled(self) -> bool:
        return self._enabled

    def set_enabled(self, on: bool) -> bool:
        if on and not self.available:
            from .errors import ApiError
            raise ApiError("이 Pi 에 OpenCV 가 없어 인식을 켤 수 없습니다. " + self.error, 501)
        self._enabled = bool(on)
        if not on:
            with self._lock:
                self.latest = []
        return self._enabled

    def configure(self, every: Optional[int] = None, conf: Optional[float] = None) -> None:
        if every is not None:
            self.every = max(1, int(every))
        if conf is not None:
            self.conf = max(0.05, min(0.99, float(conf)))
            if self.engine is not None and hasattr(self.engine, "conf"):
                self.engine.conf = self.conf

    def stop(self) -> None:
        self._stop.set()
        self._wake.set()

    # ── FrameHub 필터 ──
    def annotate(self, jpeg: bytes) -> bytes:
        if not self._enabled or self.engine is None:
            return jpeg
        self._n += 1
        if self._n % self.every == 0:
            with self._lock:
                self._pending = jpeg          # 큐 깊이 1: 느리면 최신 것만
            self._wake.set()
        dets = self.latest
        if not dets or time.time() - self.latest_t > 2.0:
            return jpeg
        try:
            return self.engine.draw(jpeg, dets, self.quality)
        except Exception:
            return jpeg

    def _worker(self) -> None:
        while not self._stop.is_set():
            self._wake.wait(0.5)
            self._wake.clear()
            with self._lock:
                jpeg, self._pending = self._pending, None
            if jpeg is None or not self._enabled:
                continue
            t0 = time.time()
            try:
                dets = self.engine.detect(jpeg)
            except Exception:
                dets = []
            dt = time.time() - t0
            with self._lock:
                self.latest = dets
                self.latest_t = time.time()
                self.infer_ms = dt * 1000
                self._times.append(self.latest_t)

    # ── 조회 ──
    def fps(self) -> float:
        if len(self._times) < 2:
            return 0.0
        span = self._times[-1] - self._times[0]
        return (len(self._times) - 1) / span if span > 0 else 0.0

    def summary(self) -> str:
        if not self.available:
            return "인식 불가 (OpenCV 없음)"
        if not self._enabled:
            return "인식 꺼짐"
        dets = self.latest
        if not dets or time.time() - self.latest_t > 2.0:
            return "감지된 것 없음"
        counts = {}
        for d in dets:
            k = KO.get(d.label, d.label)
            counts[k] = counts.get(k, 0) + 1
        return " · ".join(f"{k} {v}" for k, v in sorted(counts.items(), key=lambda kv: -kv[1]))

    def status(self) -> dict:
        with self._lock:
            dets = list(self.latest)
        return {
            "available": self.available, "enabled": self._enabled, "model": self.model, "error": self.error,
            "every": self.every, "conf": self.conf, "fps": round(self.fps(), 1), "infer_ms": round(self.infer_ms),
            "objects": [d.to_dict() for d in dets], "summary": self.summary(),
        }
