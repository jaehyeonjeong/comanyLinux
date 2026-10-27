"""PyQt5 화면 — Pi 에 붙은 800×480 모니터용.

┌───────────────────────────────────────────────────────────────┐
│ Pi 서보 카메라 v1.1.0        현재 +20.0° (1700 µs)   http://…:8889 │
├────────────────────────────────┬──────────────────────────────┤
│                                │ 이동 로그 (최신이 위)          │
│       카메라 영상 (560×420)      │ 17:05:01.123  1700 µs +20.0° Δ+10.0° │
│                                │ …                            │
├────────────────────────────────┴──────────────────────────────┤
│ picamera2 · 640×480 · 10 fps · 클라이언트 1 · pigpiod 연결됨      │
└───────────────────────────────────────────────────────────────┘
영상은 100 ms 타이머로 FrameHub 의 최신 JPEG 한 장을 QPixmap 으로 바꿔 그린다. 로그는 MovementLog 리스너 → Qt 시그널로 UI 스레드에 전달.
"""
import socket
import time

from PyQt5.QtCore import Qt, QTimer, pyqtSignal, QObject
from PyQt5.QtGui import QPixmap, QFont, QColor
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QLabel, QListWidget, QListWidgetItem,
                             QHBoxLayout, QVBoxLayout)

from .movement_log import Entry


def _local_ip() -> str:
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return "127.0.0.1"


class _Bridge(QObject):
    entry = pyqtSignal(object)


class DisplayWindow(QMainWindow):
    def __init__(self, hub, log, poller, http_port: int, version: str, gpio: int, fullscreen: bool,
                 recorder=None, detector=None):
        super().__init__()
        self.hub, self.log, self.poller = hub, log, poller
        self.recorder, self.detector = recorder, detector
        self.setWindowTitle(f"Pi 서보 카메라 v{version}")
        self.setStyleSheet("QMainWindow{background:#101214;color:#e8e8e8} QLabel{color:#e8e8e8} "
                           "QListWidget{background:#181b1f;color:#e8e8e8;border:1px solid #333;font-size:12px}")
        root = QWidget()
        self.setCentralWidget(root)
        v = QVBoxLayout(root)
        v.setContentsMargins(8, 6, 8, 6)
        v.setSpacing(6)

        # 상단 바
        top = QHBoxLayout()
        title = QLabel(f"Pi 서보 카메라  v{version}")
        title.setFont(QFont("Sans", 13, QFont.Bold))
        self.angle_label = QLabel("현재 ?")
        self.angle_label.setFont(QFont("Sans", 20, QFont.Bold))
        self.angle_label.setAlignment(Qt.AlignCenter)
        url = QLabel(f"http://{_local_ip()}:{http_port}")
        url.setStyleSheet("color:#9cf")
        self.rec_label = QLabel("")                     # "● REC 00:01:23 · 12 MB" (녹화 중만)
        self.rec_label.setStyleSheet("color:#ff5252;font-weight:bold")
        top.addWidget(title, 2)
        top.addWidget(self.angle_label, 3)
        top.addWidget(self.rec_label, 1)
        top.addWidget(url, 2, Qt.AlignRight)
        v.addLayout(top)

        # 본문: 영상 + 로그
        body = QHBoxLayout()
        self.video = QLabel("카메라 대기 중…")
        self.video.setAlignment(Qt.AlignCenter)
        self.video.setMinimumSize(320, 240)
        self.video.setStyleSheet("background:#000;border:1px solid #333")
        body.addWidget(self.video, 7)
        right = QVBoxLayout()
        lbl = QLabel(f"카메라 이동 로그 (GPIO{gpio}, 최신이 위)")
        lbl.setFont(QFont("Sans", 10, QFont.Bold))
        right.addWidget(lbl)
        self.list = QListWidget()
        right.addWidget(self.list, 1)
        body.addLayout(right, 3)
        v.addLayout(body, 1)

        # 하단 상태줄
        self.status = QLabel("")
        self.status.setStyleSheet("color:#aaa;font-size:12px")
        v.addWidget(self.status)

        # 로그 → UI 스레드
        self._bridge = _Bridge()
        self._bridge.entry.connect(self._add_entry)
        for e in log.recent(100):
            self._add_entry(e)
        log.add_listener(self._bridge.entry.emit)

        self._timer = QTimer(self)
        self._timer.timeout.connect(self._tick)
        self._timer.start(100)
        self._last_seq = -1

        if fullscreen:
            self.showFullScreen()
        else:
            self.resize(800, 480)
            self.show()

    def keyPressEvent(self, ev):
        if ev.key() in (Qt.Key_Escape, Qt.Key_Q):
            self.close()
        elif ev.key() == Qt.Key_F11:
            self.showNormal() if self.isFullScreen() else self.showFullScreen()
        else:
            super().keyPressEvent(ev)

    def _add_entry(self, e: Entry) -> None:
        item = QListWidgetItem(e.text())
        if e.kind == "rec":
            item.setForeground(QColor("#ff8a80"))
        elif e.kind == "off":
            item.setForeground(QColor("#888"))
        elif e.kind == "move" and e.delta is not None and abs(e.delta) >= 20:
            item.setForeground(QColor("#ffb347"))
        elif e.kind == "on" or e.kind == "start":
            item.setForeground(QColor("#8fd18f"))
        self.list.insertItem(0, item)
        while self.list.count() > 300:
            self.list.takeItem(self.list.count() - 1)

    def _tick(self) -> None:
        jpeg, seq = self.hub.latest()
        if jpeg is not None and seq != self._last_seq:
            self._last_seq = seq
            pm = QPixmap()
            if pm.loadFromData(jpeg, "JPEG"):
                self.video.setPixmap(pm.scaled(self.video.size(), Qt.KeepAspectRatio, Qt.FastTransformation))
        pulse, angle = self.log.current_pulse, self.log.current_angle
        if pulse:
            self.angle_label.setText(f"현재 {angle:+.1f}°  ({pulse} µs)")
            self.angle_label.setStyleSheet("color:#8fd18f")
        elif pulse == 0:
            self.angle_label.setText(f"출력 꺼짐  (마지막 {angle:+.1f}°)" if angle is not None else "출력 꺼짐")
            self.angle_label.setStyleSheet("color:#aaa")
        else:
            self.angle_label.setText("서보 상태 ?")
            self.angle_label.setStyleSheet("color:#aaa")
        if self.hub.backend == "none":
            cam = "카메라 없음 (모듈 연결 후 pi_display 재시작)"
        else:
            cam = f"{self.hub.backend} · {self.hub.width}×{self.hub.height} · {self.hub.fps():.0f} fps"
        if self.hub.age > 3:
            cam += " · 프레임 끊김"
        pig = f"pigpiod {'연결됨' if self.poller.connected else '연결 안 됨'} ({self.poller.backend})"
        extra = ""
        if self.recorder is not None:
            st = self.recorder.status()
            if st["recording"]:
                s = int(st["seconds"])
                self.rec_label.setText(f"● REC {s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d} · {st['bytes'] / 1048576:.0f} MB")
            else:
                self.rec_label.setText("")
        if self.detector is not None and self.detector.enabled:
            extra = f"   |   인식 {self.detector.summary()}"
        self.status.setText(f"{cam}   |   {pig}   |   로그 {self.log.total}건{extra}   |   {time.strftime('%Y-%m-%d %H:%M:%S')}   ESC 종료")


def run_ui(hub, log, poller, http_port: int, version: str, gpio: int, fullscreen: bool, recorder=None, detector=None) -> int:
    app = QApplication.instance() or QApplication([])
    win = DisplayWindow(hub, log, poller, http_port, version, gpio, fullscreen, recorder=recorder, detector=detector)
    _ = win
    return app.exec_()
