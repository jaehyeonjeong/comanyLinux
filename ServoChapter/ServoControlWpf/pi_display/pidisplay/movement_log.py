"""카메라(서보 팬) 이동 로그.

ServoPoller 가 읽어 온 펄스폭을 observe() 로 넘기면, **바뀌었을 때만** 기록한다:
    move  : 1600 → 1700 µs (+10.0° → +20.0°, Δ+10.0°)
    off   : n → 0           출력 꺼짐 (서보는 마지막 자리에 머묾)
    on    : 0 → n           출력 켜짐
    start : 프로그램 시작 시 현재 상태 한 줄
메모(note)는 NoteListener 가 UDP 로 받은 "WPF +20.0°" 같은 글로, 1초 안에 일어나는 다음 변화에 붙는다.

메모리에는 최근 maxlen 개, 파일에는 전부(5 MB 넘으면 .1 로 회전). 순수 파이썬이라 단위 테스트가 쉽다.
"""
import os
import threading
import time
from collections import deque
from dataclasses import dataclass
from typing import Callable, Deque, List, Optional

from .servo_spec import ServoSpec


@dataclass(frozen=True)
class Entry:
    t: float            # time.time()
    kind: str           # 'start' | 'move' | 'off' | 'on' | 'unknown'
    pulse: int          # 지금 펄스 (0 = 꺼짐)
    angle: Optional[float]   # 지금 각도 (꺼짐이면 마지막 각도)
    delta: Optional[float]   # 이전 각도와의 차이 (move 일 때)
    note: str = ""

    def text(self) -> str:
        ts = time.strftime("%H:%M:%S", time.localtime(self.t)) + f".{int((self.t % 1) * 1000):03d}"
        ang = f"{self.angle:+.1f}°" if self.angle is not None else "?"
        if self.kind == "move":
            body = f"{self.pulse} µs  {ang}  Δ{self.delta:+.1f}°"
        elif self.kind == "off":
            body = f"출력 꺼짐  (마지막 {ang})"
        elif self.kind == "on":
            body = f"출력 켜짐  {self.pulse} µs  {ang}"
        elif self.kind == "start":
            body = f"시작  {self.pulse} µs  {ang}" if self.pulse else f"시작  출력 꺼짐"
        else:
            body = "서보 상태 알 수 없음 (아직 펄스를 보낸 적 없음)"
        return f"{ts}  {body}" + (f"  [{self.note}]" if self.note else "")

    def file_line(self) -> str:
        d = time.strftime("%Y-%m-%d ", time.localtime(self.t))
        return d + self.text()


class MovementLog:
    MAX_FILE_BYTES = 5 * 1024 * 1024

    def __init__(self, spec: ServoSpec, path: Optional[str] = None, maxlen: int = 500, version: str = ""):
        self.spec = spec
        self.path = path
        self.entries: Deque[Entry] = deque(maxlen=maxlen)
        self._listeners: List[Callable[[Entry], None]] = []
        self._lock = threading.Lock()
        self._last_pulse: Optional[int] = None      # None = 알 수 없음 (PI_NOT_SERVO_GPIO)
        self._last_angle: Optional[float] = None
        self._has_obs = False                        # 첫 관측은 값이 무엇이든 한 줄 남긴다
        self._pending_note = ""
        self._pending_until = 0.0
        self.total = 0
        if path:
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            self._append_file(time.strftime("%Y-%m-%d %H:%M:%S") + f"  --- pi_display v{version} 시작 ---")

    # ── 입력 ──
    def add_listener(self, cb: Callable[[Entry], None]) -> None:
        self._listeners.append(cb)

    def set_pending_note(self, text: str, ttl: float = 1.0, now: Optional[float] = None) -> None:
        now = time.time() if now is None else now
        with self._lock:
            self._pending_note = text.strip()[:60]
            self._pending_until = now + ttl

    @property
    def current_pulse(self) -> Optional[int]:
        return self._last_pulse

    @property
    def current_angle(self) -> Optional[float]:
        return self._last_angle

    def observe(self, pulse: Optional[int], t: Optional[float] = None) -> Optional[Entry]:
        """폴러가 읽은 펄스. None = 알 수 없음(PI_NOT_SERVO_GPIO 등). 바뀌었을 때만 Entry 를 돌려준다."""
        t = time.time() if t is None else t
        with self._lock:
            note = self._pending_note if t <= self._pending_until else ""
            if self._has_obs and pulse == self._last_pulse:
                return None
            first = not self._has_obs
            self._has_obs = True
            prev_pulse, prev_angle = self._last_pulse, self._last_angle
            if pulse is None:
                entry = Entry(t, "unknown", 0, prev_angle, None, note)
            elif first:
                angle = self.spec.pulse_to_angle(pulse) if pulse else None
                entry = Entry(t, "start", pulse, angle, None, note)
            elif pulse == 0:
                entry = Entry(t, "off", 0, prev_angle, None, note)
            else:
                angle = self.spec.pulse_to_angle(pulse)
                if not prev_pulse:                       # 꺼짐(0) 또는 알 수 없음(None) 에서 켜짐
                    entry = Entry(t, "on", pulse, angle, None, note)
                else:
                    delta = angle - prev_angle if prev_angle is not None else None
                    entry = Entry(t, "move", pulse, angle, delta, note)
            self._last_pulse = pulse
            if pulse:
                self._last_angle = self.spec.pulse_to_angle(pulse)
            if note:
                self._pending_note = ""
            self.entries.append(entry)
            self.total += 1
        self._append_file(entry.file_line())
        for cb in list(self._listeners):
            try:
                cb(entry)
            except Exception:       # 리스너 하나의 오류가 로그 전체를 멈추지 않게
                pass
        return entry

    # ── 조회 ──
    def recent(self, n: int = 20) -> List[Entry]:
        with self._lock:
            return list(self.entries)[-n:]

    # ── 파일 ──
    def _append_file(self, line: str) -> None:
        if not self.path:
            return
        try:
            if os.path.exists(self.path) and os.path.getsize(self.path) > self.MAX_FILE_BYTES:
                os.replace(self.path, self.path + ".1")
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError:
            pass
