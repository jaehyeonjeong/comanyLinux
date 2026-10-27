"""카메라가 만든 최신 JPEG 한 장을 여러 소비자(HTTP 클라이언트들, 화면)가 함께 쓰는 보관함.

항상 "최신 1장" 만 가지고 있으므로 느린 소비자가 있어도 지연이 쌓이지 않는다 (밀린 프레임은 버림).
"""
import threading
import time
from collections import deque
from typing import Optional, Tuple


class FrameHub:
    def __init__(self):
        self._cond = threading.Condition()
        self._jpeg: Optional[bytes] = None
        self._seq = 0
        self._ts = 0.0
        self._times = deque(maxlen=60)
        self.width = 0
        self.height = 0
        self.backend = "-"

    def publish(self, jpeg: bytes, width: int = 0, height: int = 0) -> None:
        now = time.time()
        with self._cond:
            self._jpeg = jpeg
            self._seq += 1
            self._ts = now
            self._times.append(now)
            if width:
                self.width, self.height = width, height
            self._cond.notify_all()

    def latest(self) -> Tuple[Optional[bytes], int]:
        with self._cond:
            return self._jpeg, self._seq

    def wait_next(self, last_seq: int, timeout: float = 1.0) -> Tuple[Optional[bytes], int]:
        """last_seq 보다 새 프레임이 올 때까지(최대 timeout) 기다린다. 없으면 (None, last_seq)."""
        with self._cond:
            if self._seq <= last_seq:
                self._cond.wait(timeout)
            if self._seq > last_seq and self._jpeg is not None:
                return self._jpeg, self._seq
            return None, last_seq

    @property
    def seq(self) -> int:
        return self._seq

    @property
    def age(self) -> float:
        """마지막 프레임이 온 뒤 흐른 시간(초)"""
        return time.time() - self._ts if self._ts else float("inf")

    def fps(self) -> float:
        with self._cond:
            if len(self._times) < 2:
                return 0.0
            span = self._times[-1] - self._times[0]
            return (len(self._times) - 1) / span if span > 0 else 0.0
