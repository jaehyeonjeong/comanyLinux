#!/usr/bin/env python3
"""가짜 pigpiod (리눅스/WSL 용, 표준 라이브러리만 사용)

Raspberry Pi 가 없을 때 WSL(또는 아무 리눅스)을 "가짜 Pi" 로 만들어 준다.
실제 pigpiod 와 같은 16바이트 소켓 규약으로 응답하므로 ServoControl(Qt) · ServoControlWpf 모두 그대로 접속된다.

    요청 16바이트 = cmd, p1, p2, p3   (uint32 little-endian)
    응답 16바이트 = cmd, p1, p2, res  (res: int32, 0 이상 성공 / 음수 오류)

흉내 내는 명령: SERVO(8) 펄스폭 검사(0 또는 500~2500, gpio 0~31), HWVER(17)=0xc03111, PIGPV(26)=79

사용법:
    python3 fake_pigpiod.py                     # 0.0.0.0:8888  (Windows 에서 127.0.0.1:8888 로 접속 가능 — WSL2 localhost 포워딩)
    python3 fake_pigpiod.py --port 8888 --bind 127.0.0.1
WSL 에 sshd 를 설치했다면 WPF 의 "WSL 터널" 모드(ssh 별명 → 127.0.0.1)로도 이 서버에 닿는다.
"""
import argparse
import socket
import struct
import threading
import time

CMD_SERVO, CMD_HWVER, CMD_PIGPV = 8, 17, 26
HW_VER, PIGPIO_VER = 0xC03111, 79          # Pi 4B 리비전, pigpio 버전
SPEED_DEG_PER_S = 60 / 0.19                 # HS-311 @4.8 V

pulses = {}            # gpio -> 펄스폭 (0 = 꺼짐)
angles = {}            # gpio -> 흉내 낸 현재 각도
stamps = {}
lock = threading.Lock()


def advance(gpio):
    """목표 각도(1500 µs = 0°, 10 µs/°)를 향해 서보 속도로 움직인 현재 각도"""
    now = time.monotonic()
    pulse = pulses.get(gpio, 0)
    cur = angles.get(gpio, 0.0)
    last = stamps.get(gpio, now)
    stamps[gpio] = now
    if pulse == 0:
        return cur
    target = (pulse - 1500) / 10.0
    step = SPEED_DEG_PER_S * (now - last)
    diff = target - cur
    angles[gpio] = target if abs(diff) <= step else cur + (step if diff > 0 else -step)
    return angles[gpio]


def handle(cmd, p1, p2):
    if cmd == CMD_SERVO:
        if p1 > 31:
            return -2, f"SERVO gpio={p1} → PI_BAD_USER_GPIO"
        if p2 != 0 and not 500 <= p2 <= 2500:
            return -7, f"SERVO gpio={p1} pulse={p2} → PI_BAD_PULSEWIDTH"
        with lock:
            advance(p1)
            pulses[p1] = p2
        return 0, (f"SERVO gpio={p1} 출력 끔" if p2 == 0 else f"SERVO gpio={p1} {p2} µs → 목표 {(p2 - 1500) / 10:+.1f}°")
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
            cmd, p1, p2, _ = struct.unpack("<IIII", buf)
            res, desc = handle(cmd, p1, p2)
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
    a = ap.parse_args()
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((a.bind, a.port))
    srv.listen(5)
    print(f"가짜 pigpiod 실행 중: {a.bind}:{a.port}   (Ctrl+C 로 종료)", flush=True)
    print("흉내 내는 명령: SERVO(8) HWVER(17)=0xc03111 PIGPV(26)=79 · 펄스 범위 0, 500~2500 µs", flush=True)
    try:
        while True:
            conn, addr = srv.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            threading.Thread(target=serve_client, args=(conn, addr), daemon=True).start()
    except KeyboardInterrupt:
        print("\n종료")
    finally:
        srv.close()


if __name__ == "__main__":
    main()
