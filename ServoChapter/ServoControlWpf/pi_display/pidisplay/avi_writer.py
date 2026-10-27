"""순수 파이썬 AVI(MJPG) 기록기 — ffmpeg·OpenCV 없이 JPEG 프레임을 그대로 동영상 파일로 만든다.

picamera2 가 없는 백엔드(libcamera-vid·legacy·opencv·fake)에서 녹화할 때, 그리고 WSL 에서 전체 흐름을 시험할 때 쓴다.
MJPEG 는 "JPEG 를 이어 붙인 것" 이므로 재인코딩이 없어 CPU 를 거의 쓰지 않는다. Windows 미디어 플레이어·VLC 가 바로 연다.

파일 구조 (RIFF):
    RIFF <len> 'AVI '
      LIST 'hdrl'  avih(56)  LIST 'strl' { strh(56) strf(40=BITMAPINFOHEADER, 'MJPG') }
      LIST 'movi'  '00dc' <len> JPEG  (짝수 길이로 패딩) …
      idx1         프레임마다 16바이트 (fourcc, flags=KEYFRAME, movi 안 오프셋, 길이)
길이 필드는 close() 때 되돌아가 채운다. 실제 측정한 fps(프레임 수 / 걸린 시간)를 strh 의 rate/scale 에 넣어 재생 속도가 실시간과 맞게 한다.
"""
import struct
import time
from typing import Optional

AVIF_HASINDEX = 0x10
AVIIF_KEYFRAME = 0x10


class AviMjpegWriter:
    def __init__(self, path: str, width: int, height: int, fps: float = 10.0, clock=time.time):
        self.path = path
        self.width, self.height = int(width), int(height)
        self.nominal_fps = max(1.0, float(fps))
        self._clock = clock
        self.frames = 0
        self.bytes = 0
        self._first_t: Optional[float] = None
        self._last_t: Optional[float] = None
        self._idx = []                      # (offset, size)
        self._f = open(path, "wb")
        self._write_headers()

    # ── 헤더 ──
    def _write_headers(self) -> None:
        f = self._f
        f.write(b"RIFF" + struct.pack("<I", 0) + b"AVI ")
        self._hdrl_pos = f.tell()
        f.write(b"LIST" + struct.pack("<I", 0) + b"hdrl")
        self._avih_pos = f.tell()
        f.write(b"avih" + struct.pack("<I", 56) + self._avih(0, self.nominal_fps))
        strl_pos = f.tell()
        f.write(b"LIST" + struct.pack("<I", 0) + b"strl")
        self._strh_pos = f.tell()
        f.write(b"strh" + struct.pack("<I", 56) + self._strh(0, self.nominal_fps))
        f.write(b"strf" + struct.pack("<I", 40) + self._strf())
        self._patch_size(strl_pos, f.tell())
        self._patch_size(self._hdrl_pos, f.tell())
        self._movi_pos = f.tell()
        f.write(b"LIST" + struct.pack("<I", 0) + b"movi")

    def _avih(self, frames: int, fps: float) -> bytes:
        us_per_frame = int(round(1_000_000 / fps))
        max_bps = int(self.bytes / max(1e-6, self._duration())) if frames else 0
        return struct.pack("<IIIIIIIIII4I", us_per_frame, max_bps, 0, AVIF_HASINDEX, frames, 0, 1,
                           self._max_chunk(), self.width, self.height, 0, 0, 0, 0)

    def _strh(self, frames: int, fps: float) -> bytes:
        scale, rate = 1000, int(round(fps * 1000))
        return (b"vids" + b"MJPG" + struct.pack("<IHHIIIIIIII", 0, 0, 0, 0, scale, rate, 0, frames,
                                                  self._max_chunk(), 10000, 0)
                + struct.pack("<HHHH", 0, 0, self.width, self.height))

    def _strf(self) -> bytes:
        return struct.pack("<IiiHH4sIiiII", 40, self.width, self.height, 1, 24, b"MJPG",
                           self.width * self.height * 3, 0, 0, 0, 0)

    def _max_chunk(self) -> int:
        return max((s for _, s in self._idx), default=0)

    def _patch_size(self, list_pos: int, end: int) -> None:
        """LIST/RIFF 청크의 길이 = (끝 − (pos+8))"""
        cur = self._f.tell()
        self._f.seek(list_pos + 4)
        self._f.write(struct.pack("<I", end - (list_pos + 8)))
        self._f.seek(cur)

    def _duration(self) -> float:
        if self._first_t is None or self._last_t is None:
            return 0.0
        return max(0.0, self._last_t - self._first_t)

    # ── 프레임 ──
    def add_frame(self, jpeg: bytes) -> None:
        now = self._clock()
        if self._first_t is None:
            self._first_t = now
        self._last_t = now
        f = self._f
        offset = f.tell() - (self._movi_pos + 8)         # 'movi' fourcc 기준 (대부분의 플레이어가 기대하는 값)
        f.write(b"00dc" + struct.pack("<I", len(jpeg)) + jpeg)
        if len(jpeg) % 2:
            f.write(b"\x00")
        self._idx.append((offset, len(jpeg)))
        self.frames += 1
        self.bytes += len(jpeg)
        if self.frames % 10 == 0:
            f.flush()                       # 녹화 중 파일 크기(status 의 bytes)가 실제와 맞게

    # ── 마무리 ──
    def measured_fps(self) -> float:
        d = self._duration()
        if self.frames >= 2 and d > 0:
            return (self.frames - 1) / d
        return self.nominal_fps

    def close(self) -> None:
        if self._f is None:
            return
        f = self._f
        self._patch_size(self._movi_pos, f.tell())
        # idx1
        f.write(b"idx1" + struct.pack("<I", 16 * len(self._idx)))
        for off, size in self._idx:
            f.write(b"00dc" + struct.pack("<III", AVIIF_KEYFRAME, off, size))
        end = f.tell()
        self._patch_size(0, end)
        fps = self.measured_fps()
        f.seek(self._avih_pos + 8)
        f.write(self._avih(self.frames, fps))
        f.seek(self._strh_pos + 8)
        f.write(self._strh(self.frames, fps))
        f.close()
        self._f = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def read_avi_info(path: str) -> dict:
    """검증·테스트용: 헤더만 읽어 {width,height,frames,fps,fourcc,index_entries} 를 돌려준다."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"RIFF" or data[8:12] != b"AVI ":
        raise ValueError("AVI 파일이 아닙니다")
    riff_len = struct.unpack("<I", data[4:8])[0]
    pos = data.find(b"avih")
    avih = struct.unpack("<IIIIIIIIII4I", data[pos + 8:pos + 8 + 56])
    pos = data.find(b"strh")
    strh = data[pos + 8:pos + 8 + 56]
    fcc_type, handler = strh[:4], strh[4:8]
    _, _, _, _, scale, rate, _, length = struct.unpack("<IHHIIIII", strh[8:36])
    pos = data.find(b"idx1")
    idx_len = struct.unpack("<I", data[pos + 4:pos + 8])[0]
    return {
        "riff_len": riff_len, "file_len": len(data),
        "width": avih[8], "height": avih[9], "frames": avih[4],
        "fps": rate / scale if scale else 0.0, "fcc_type": fcc_type.decode(), "fourcc": handler.decode(),
        "stream_frames": length, "index_entries": idx_len // 16,
    }
