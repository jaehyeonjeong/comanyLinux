#!/usr/bin/env python3
"""가짜 pigpiod (리눅스/WSL 용, 표준 라이브러리만 사용)

Raspberry Pi 가 없을 때 WSL(또는 아무 리눅스)을 "가짜 Pi" 로 만들어 준다.
실제 pigpiod 와 같은 16바이트 소켓 규약으로 응답하므로 ServoControl(Qt) · ServoControlWpf 모두 그대로 접속된다.

    요청 16바이트 = cmd, p1, p2, p3   (uint32 little-endian)
    응답 16바이트 = cmd, p1, p2, res  (res: int32, 0 이상 성공 / 음수 오류)

흉내 내는 명령: SERVO(8) 펄스폭 검사(0 또는 500~2500, gpio 0~31), HWVER(17)=0xc03111, PIGPV(26)=79,
                GPW(84) 지금 펄스폭 읽기 (접속이 끊겨도 기억 → 다시 접속한 프로그램이 이전 위치를 알 수 있음)

사용법:
    python3 fake_pigpiod.py                     # 0.0.0.0:8888  (Windows 에서 127.0.0.1:8888 로 접속 가능 — WSL2 localhost 포워딩)
    python3 fake_pigpiod.py --port 8888 --bind 127.0.0.1
WSL 에 sshd 를 설치했다면 WPF 의 "WSL 터널" 모드(ssh 별명 → 127.0.0.1)로도 이 서버에 닿는다.
"""
import argparse
import errno
import json
import os
import socket
import struct
import subprocess
import sys
import threading
import time

# 상태 파일: 펄스·각도를 저장해 껐다 켜도 "같은 서보" 가 같은 자리에 있게 한다 (--state 로 바꿀 수 있음)
DEFAULT_STATE = os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")), "VI", "fake_pigpiod.json")
state_file = None

CMD_SERVO, CMD_HWVER, CMD_PIGPV, CMD_GPW, CMD_CF1 = 8, 17, 26, 84, 87
HW_VER, PIGPIO_VER = 0xC03111, 79          # Pi 4B 리비전, pigpio 버전
SPEED_DEG_PER_S = 60 / 0.19                 # HS-311 @4.8 V

pulses = {}            # gpio -> 펄스폭 (0 = 꺼짐)
angles = {}            # gpio -> 흉내 낸 현재 각도
stamps = {}
lock = threading.Lock()


class Calib:
    """펄스 → 각도 보정값 (WPF ServoSpec · pi_display --calib 와 같은 공식·문자열 형식 "600,1500,2400,-80,80").
    실제 pigpiod 는 펄스만 알고 각도는 모른다 — 각도는 보는 쪽이 계산하므로 WPF 와 같은 값을 써야 같은 숫자가 나온다.
    WPF 는 CF1(87) 명령으로 보정값을 보낸다: p1=1 펄스(p2 = min | neutral<<16, p3 = max), p1=2 각도×10(p2, p3 int32)."""

    def __init__(self, min_pulse=1100, neutral=1500, max_pulse=1900, min_angle=-40.0, max_angle=40.0):
        self.min_pulse, self.neutral, self.max_pulse = int(min_pulse), int(neutral), int(max_pulse)
        self.min_angle, self.max_angle = float(min_angle), float(max_angle)

    def is_valid(self):
        return (500 <= self.min_pulse < self.neutral < self.max_pulse <= 2500
                and -90 <= self.min_angle < 0 < self.max_angle <= 90)

    def pulse_to_angle(self, pulse):
        if pulse >= self.neutral:
            return (pulse - self.neutral) * self.max_angle / float(self.max_pulse - self.neutral)
        return -(self.neutral - pulse) * -self.min_angle / float(self.neutral - self.min_pulse)

    def text(self):
        return f"{self.min_pulse},{self.neutral},{self.max_pulse},{self.min_angle:g},{self.max_angle:g}"

    def __str__(self):
        return f"{self.min_pulse}~{self.neutral}~{self.max_pulse} µs, {self.min_angle:g}°~{self.max_angle:g}°"

    @staticmethod
    def parse(text):
        parts = [p.strip() for p in str(text).split(",")]
        if len(parts) != 5:
            raise ValueError("--calib 는 '최소펄스,중립펄스,최대펄스,최소각,최대각' 다섯 값이어야 합니다")
        c = Calib(int(parts[0]), int(parts[1]), int(parts[2]), float(parts[3]), float(parts[4]))
        if not c.is_valid():
            raise ValueError("보정값이 올바르지 않습니다 (500~2500 µs, 최소<중립<최대, 최소각<0<최대각)")
        return c


calib = Calib()


def _signed(u):
    return u - (1 << 32) if u >= (1 << 31) else u


def advance(gpio):
    """목표 각도(보정값 기준)를 향해 서보 속도로 움직인 현재 각도"""
    now = time.monotonic()
    pulse = pulses.get(gpio, 0)
    cur = angles.get(gpio, 0.0)
    last = stamps.get(gpio, now)
    stamps[gpio] = now
    if pulse == 0:
        return cur
    target = calib.pulse_to_angle(pulse)
    step = SPEED_DEG_PER_S * (now - last)
    diff = target - cur
    angles[gpio] = target if abs(diff) <= step else cur + (step if diff > 0 else -step)
    return angles[gpio]


def load_state():
    """상태 파일 → pulses/angles(/calib). 펄스가 켜져 있던 핀은 그동안 목표에 도달했다고 본다."""
    global calib
    if not state_file or not os.path.exists(state_file):
        return
    try:
        with open(state_file, encoding="utf-8") as f:
            saved = json.load(f)
        with lock:
            if isinstance(saved.get("calib"), str):
                try:
                    calib = Calib.parse(saved["calib"])
                except ValueError:
                    pass
            for key, s in saved.items():
                if not str(key).isdigit() or not isinstance(s, dict):
                    continue
                gpio, pulse, angle = int(key), int(s.get("Pulse", 0)), float(s.get("Angle", 0.0))
                if not 0 <= gpio <= 31 or (pulse != 0 and not 500 <= pulse <= 2500):
                    continue
                pulses[gpio] = pulse
                angles[gpio] = calib.pulse_to_angle(pulse) if pulse else max(-90.0, min(90.0, angle))
                stamps[gpio] = time.monotonic()
    except (OSError, ValueError, AttributeError):
        pass


def save_state():
    if not state_file:
        return
    with lock:
        snap = {str(g): {"Pulse": p, "Angle": advance(g)} for g, p in pulses.items()}
        snap["calib"] = calib.text()
    try:
        os.makedirs(os.path.dirname(state_file), exist_ok=True)
        with open(state_file, "w", encoding="utf-8") as f:
            json.dump(snap, f, indent=2)
    except OSError:
        pass


def set_calib(new):
    """보정값 교체 — 기억 중인 각도를 새 공식으로 다시 계산(펄스는 그대로), 상태 파일에 저장"""
    global calib
    with lock:
        calib = new
        for g, p in pulses.items():
            if p:
                angles[g] = new.pulse_to_angle(p)
    save_state()


def handle(cmd, p1, p2, p3=0):
    if cmd == CMD_CF1:                      # WPF 가 보정값을 알려 줌 (ServoCalibration.cs 와 같은 패킹)
        if p1 == 1:
            c = Calib(p2 & 0xFFFF, p2 >> 16, p3 & 0xFFFF, calib.min_angle, calib.max_angle)
            if not (500 <= c.min_pulse < c.neutral < c.max_pulse <= 2500):
                return -7, f"CF1 펄스 ({c.min_pulse}/{c.neutral}/{c.max_pulse}) → 보정값으로 쓸 수 없음"
            set_calib(c)
            return 0, f"CALIB 펄스 {c.min_pulse}/{c.neutral}/{c.max_pulse} µs"
        if p1 == 2:
            c = Calib(calib.min_pulse, calib.neutral, calib.max_pulse, _signed(p2) / 10.0, _signed(p3) / 10.0)
            if not (-90 <= c.min_angle < 0 < c.max_angle <= 90):
                return -7, f"CF1 각도 ({c.min_angle:g}~{c.max_angle:g}) → 보정값으로 쓸 수 없음"
            set_calib(c)
            return 0, f"CALIB 각도 {c.min_angle:g}°~{c.max_angle:g}° → 보정값 {c}"
        return 0, f"CF1 p1={p1} → 0 (모르는 하위 명령)"
    if cmd == CMD_SERVO:
        if p1 > 31:
            return -2, f"SERVO gpio={p1} → PI_BAD_USER_GPIO"
        if p2 != 0 and not 500 <= p2 <= 2500:
            return -7, f"SERVO gpio={p1} pulse={p2} → PI_BAD_PULSEWIDTH"
        with lock:
            advance(p1)
            changed = pulses.get(p1) != p2
            pulses[p1] = p2
        if changed:
            save_state()
        return 0, (f"SERVO gpio={p1} 출력 끔" if p2 == 0 else f"SERVO gpio={p1} {p2} µs → 목표 {calib.pulse_to_angle(p2):+.1f}°")
    if cmd == CMD_GPW:                      # 지금 펄스폭 (0 = 꺼짐), 서보로 쓴 적 없으면 -93 (PI_NOT_SERVO_GPIO)
        if p1 > 31:
            return -2, f"GPW gpio={p1} → PI_BAD_USER_GPIO"
        with lock:
            if p1 not in pulses:
                return -93, f"GPW gpio={p1} → PI_NOT_SERVO_GPIO"
            pw = pulses[p1]
        return pw, f"GPW gpio={p1} → {pw} µs" if pw else f"GPW gpio={p1} → 0 (출력 꺼짐)"
    if cmd == CMD_HWVER:
        return HW_VER, f"HWVER → 0x{HW_VER:x}"
    if cmd == CMD_PIGPV:
        return PIGPIO_VER, f"PIGPV → {PIGPIO_VER}"
    return 0, f"cmd {cmd} (p1={p1}, p2={p2}) → 0 (흉내 내지 않는 명령)"


def serve_client(conn, addr):
    print(f"● 접속 {addr[0]}:{addr[1]}", flush=True)
    try:
        while True:
            buf = b""
            while len(buf) < 16:
                chunk = conn.recv(16 - len(buf))
                if not chunk:
                    return
                buf += chunk
            cmd, p1, p2, p3 = struct.unpack("<IIII", buf)
            res, desc = handle(cmd, p1, p2, p3)
            conn.sendall(struct.pack("<IIIi", cmd, p1, p2, res))
            print(f"{time.strftime('%H:%M:%S')}  {'✗' if res < 0 else '✓'} {desc}", flush=True)
    except (ConnectionError, OSError):
        pass
    finally:
        conn.close()
        print(f"○ 해제 {addr[0]}:{addr[1]}", flush=True)


def main():
    ap = argparse.ArgumentParser(description="가짜 pigpiod")
    ap.add_argument("--port", type=int, default=8888)
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--state", default=DEFAULT_STATE, help="상태 파일 (빈 문자열이면 저장 안 함)")
    ap.add_argument("--calib", default=None,
                    help="펄스→각도 보정값 '최소펄스,중립펄스,최대펄스,최소각,최대각' (WPF 보정 탭 값과 같게; 생략하면 상태 파일의 값 또는 HS-311 공식값. WPF 가 접속하면 CF1 로 다시 맞춰 줌)")
    a = ap.parse_args()
    global state_file, calib
    state_file = a.state or None
    load_state()
    if a.calib:
        try:
            set_calib(Calib.parse(a.calib))
        except ValueError as ex:
            print(f"--calib 오류: {ex}", file=sys.stderr)
            sys.exit(2)
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind((a.bind, a.port))
    except OSError as ex:
        if ex.errno != errno.EADDRINUSE:
            raise
        # 누가 잡고 있는지 알려 준다 (이미 떠 있는 가짜 Pi 면 그대로 쓰면 됨 — WPF 의 "WSL 가짜 Pi" 가 재사용)
        who = ""
        try:
            out = subprocess.run(["ss", "-ltnp"], capture_output=True, text=True, timeout=3).stdout
            who = "\n".join("    " + line.strip() for line in out.splitlines() if f":{a.port} " in line)
        except (OSError, subprocess.SubprocessError):
            pass
        print(f"포트 {a.port} 을(를) 이미 다른 프로세스가 듣고 있습니다 (Address already in use).", file=sys.stderr)
        if who:
            print(who, file=sys.stderr)
        print("  · 그것이 fake_pigpiod.py 면 그대로 쓰면 됩니다 — WPF 의 에뮬레이터 'WSL 가짜 Pi' 가 재사용합니다.\n"
              "  · 끄려면:  pkill -f '[f]ake_pigpiod.py'      · 다른 포트로:  ./run-from-wsl.sh fakepi 18888",
              file=sys.stderr)
        sys.exit(1)
    srv.listen(5)
    print(f"가짜 pigpiod 실행 중: {a.bind}:{a.port}   (Ctrl+C 로 종료)", flush=True)
    print("흉내 내는 명령: SERVO(8) HWVER(17)=0xc03111 PIGPV(26)=79 GPW(84) · 펄스 범위 0, 500~2500 µs", flush=True)
    print(f"상태 파일: {state_file or '(없음)'}   보정값: {calib} (WPF 가 접속하면 자기 보정값으로 맞춤)", flush=True)
    for g, p in sorted(pulses.items()):
        print(f"  복원: GPIO{g} {'출력 꺼짐' if p == 0 else f'{p} µs'}, 각도 {angles.get(g, 0.0):+.1f}°", flush=True)
    try:
        while True:
            conn, addr = srv.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=serve_client, args=(conn, addr), daemon=True).start()
    except KeyboardInterrupt:
        print("\n종료")
    finally:
        save_state()
        srv.close()


if __name__ == "__main__":
    main()
