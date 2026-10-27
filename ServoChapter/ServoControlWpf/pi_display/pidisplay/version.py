"""버전: 솔루션 루트의 VERSION 파일(단일 진실)을 찾아 읽는다.

탐색 순서 (pidisplay/ 기준):  ../VERSION (Pi 에 배포된 pi_display/VERSION) → ../../VERSION (개발 PC 의 솔루션 루트)
없으면 "0.0.0-dev".
"""
import os

_HERE = os.path.dirname(os.path.abspath(__file__))
_CANDIDATES = [
    os.path.join(_HERE, "..", "VERSION"),
    os.path.join(_HERE, "..", "..", "VERSION"),
]


def read_version() -> str:
    for p in _CANDIDATES:
        try:
            with open(p, encoding="utf-8-sig") as f:
                v = f.read().strip()
            if v:
                return v
        except OSError:
            continue
    return "0.0.0-dev"


__version__ = read_version()
