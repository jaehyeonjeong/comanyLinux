"""pigpiod 에 "지금 이 핀의 서보 펄스폭" (GPW, 명령 84) 을 주기적으로 물어 MovementLog 에 넘기는 스레드.

두 가지 길:
  1) python3-pigpio 모듈이 있으면 pigpio.pi(host, port).get_servo_pulsewidth(gpio)
  2) 없으면 표준 라이브러리 소켓으로 16바이트 규약을 직접 말한다 (WPF 의 PigpioClient.cs, tools/fake_pigpiod.py 와 같은 규약)
     요청 = cmd, p1, p2, p3 (uint32 LE) / 응답 = cmd, p1, p2, res (int32 LE)

−93 (PI_NOT_SERVO_GPIO) = 아직 이 핀에 서보 펄스를 보낸 적 없음 → "알 수 없음" 으로 넘긴다.
연결이 끊기면 2초마다 다시 붙는다.
"""
import socket
import struct
import threading
import time
from typing import Optional

from .movement_log import MovementLog

CMD_GPW = 84
PI_NOT_SERVO_GPIO = -93


class _SocketPigpio:
    """stdlib 만으로 pigpiod 와 말하는 최소 클라이언트"""

    def __init__(self, host: str, port: int, timeout: float = 1.5):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def command(self, cmd: int, p1: int = 0, p2: int = 0) -> int:
        self.sock.sendall(struct.pack("<IIII", cmd, p1, p2, 0))
        buf = b""
        while len(buf) < 16:
            chunk = self.sock.recv(16 - len(buf))
            if not chunk:
                raise ConnectionError("pigpiod 가 연결을 닫았습니다")
            buf += chunk
        rcmd, _, _, res = struct.unpack("<IIIi", buf)
        if rcmd != cmd:
            raise ConnectionError("pigpiod 응답 형식이 올바르지 않습니다")
        return res

    def get_servo_pulsewidth(self, gpio: int) -> int:
        return self.command(CMD_GPW, gpio, 0)

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


class ServoPoller(threading.Thread):
    def __init__(self, log: MovementLog, host: str = "127.0.0.1", port: int = 8888, gpio: int = 18,
                 poll_ms: int = 50, prefer_module: bool = True):
        super().__init__(name="servo-poller", daemon=True)
        self.log = log
        self.host, self.port, self.gpio = host, port, gpio
        self.interval = max(0.01, poll_ms / 1000.0)
        self.prefer_module = prefer_module
        self.connected = False
        self.backend = "-"
        self.last_error = ""
        self.polls = 0
        self._stop = threading.Event()
        self._client = None

    def stop(self) -> None:
        self._stop.set()

    # ── 연결 ──
    def _connect(self) -> bool:
        if self.prefer_module:
            try:
                import pigpio  # type: ignore
                pi = pigpio.pi(self.host, self.port)
                if pi.connected:
                    self._client = pi
                    self.backend = "pigpio 모듈"
                    return True
                pi.stop()
            except ImportError:
                pass
            except Exception as ex:  # 모듈은 있는데 실패 → 소켓으로
                self.last_error = str(ex)
        try:
            self._client = _SocketPigpio(self.host, self.port)
            self.backend = "소켓(GPW)"
            return True
        except OSError as ex:
            self.last_error = str(ex)
            self._client = None
            return False

    def _disconnect(self) -> None:
        c, self._client = self._client, None
        if c is None:
            return
        try:
            if hasattr(c, "stop"):
                c.stop()
            else:
                c.close()
        except Exception:
            pass

    def read_pulse(self) -> Optional[int]:
        """지금 펄스폭. None = 알 수 없음. 통신 오류는 예외."""
        c = self._client
        if c is None:
            raise ConnectionError("not connected")
        res = c.get_servo_pulsewidth(self.gpio)
        if res == PI_NOT_SERVO_GPIO:
            return None
        if res < 0:
            raise ConnectionError(f"pigpio 오류 {res}")
        return int(res)

    # ── 루프 ──
    def run(self) -> None:
        while not self._stop.is_set():
            if self._client is None:
                if not self._connect():
                    self.connected = False
                    self._stop.wait(2.0)
                    continue
                self.connected = True
            try:
                pulse = self.read_pulse()
                self.polls += 1
                self.log.observe(pulse)
            except Exception as ex:   # pigpio.error 도 포함
                # pigpio 모듈은 −93 을 예외(pigpio.error)로 던진다
                msg = str(ex)
                if "-93" in msg or "PI_NOT_SERVO_GPIO" in msg:
                    self.log.observe(None)
                else:
                    self.last_error = msg
                    self.connected = False
                    self._disconnect()
                    self._stop.wait(2.0)
                    continue
            self._stop.wait(self.interval)
        self._disconnect()
