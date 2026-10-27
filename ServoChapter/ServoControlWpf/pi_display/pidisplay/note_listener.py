"""(선택) WPF 가 각도 명령을 보낼 때 UDP 로 같이 보내는 메모("WPF +20.0° 1700us")를 받아 다음 로그 줄에 붙인다.

없어도 로그는 동작한다 (누가 움직였는지만 빠짐).
"""
import socket
import threading

from .movement_log import MovementLog


class NoteListener(threading.Thread):
    def __init__(self, log: MovementLog, bind: str = "0.0.0.0", port: int = 8890):
        super().__init__(name="note-udp", daemon=True)
        self.log = log
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind((bind, port))
        self.sock.settimeout(0.5)
        self.received = 0
        self._stop = threading.Event()

    def stop(self) -> None:
        self._stop.set()

    def run(self) -> None:
        while not self._stop.is_set():
            try:
                data, _ = self.sock.recvfrom(256)
            except socket.timeout:
                continue
            except OSError:
                break
            text = data.decode("utf-8", errors="replace").strip()
            if text:
                self.received += 1
                self.log.set_pending_note(text)
        try:
            self.sock.close()
        except OSError:
            pass
