import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.avi_writer import AviMjpegWriter, read_avi_info  # noqa: E402
from pidisplay.camera.fake_backend import _JPEG_A, _JPEG_B  # noqa: E402


class AviWriterTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.path = os.path.join(self.tmp, "t.avi")

    def test_header_frames_and_index(self):
        now = [100.0]
        w = AviMjpegWriter(self.path, 64, 48, fps=7, clock=lambda: now[0])
        for i in range(5):
            w.add_frame(_JPEG_A if i % 2 else _JPEG_B)
            now[0] += 0.1                                   # 실제 간격 0.1 s → 10 fps 로 기록되어야 함
        w.close()
        info = read_avi_info(self.path)
        self.assertEqual((info["width"], info["height"]), (64, 48))
        self.assertEqual(info["frames"], 5)
        self.assertEqual(info["stream_frames"], 5)
        self.assertEqual(info["index_entries"], 5)
        self.assertEqual(info["fourcc"], "MJPG")
        self.assertEqual(info["fcc_type"], "vids")
        self.assertEqual(info["riff_len"], info["file_len"] - 8)       # RIFF 길이 = 파일 − 8
        self.assertAlmostEqual(info["fps"], 10.0, places=1)

    def test_odd_length_frame_is_padded(self):
        odd = _JPEG_A[:-1] + b"\x00\xd9"                 # 길이 홀수로 만들기 (내용은 상관없음)
        if len(odd) % 2 == 0:
            odd = odd + b"\x00"
        self.assertEqual(len(odd) % 2, 1)
        with AviMjpegWriter(self.path, 64, 48, fps=10) as w:
            w.add_frame(odd)
            w.add_frame(_JPEG_B)
        with open(self.path, "rb") as f:
            data = f.read()
        movi = data.find(b"movi")
        first = data.find(b"00dc", movi)
        n = struct.unpack("<I", data[first + 4:first + 8])[0]
        self.assertEqual(n, len(odd))
        second = data.find(b"00dc", first + 8 + n)
        self.assertEqual(second, first + 8 + n + 1)        # 패딩 1 바이트 뒤에 다음 청크
        self.assertEqual(second % 2, 0)

    def test_index_offsets_point_to_chunks(self):
        with AviMjpegWriter(self.path, 64, 48, fps=10) as w:
            for _ in range(3):
                w.add_frame(_JPEG_A)
        with open(self.path, "rb") as f:
            data = f.read()
        movi = data.find(b"LIST", data.find(b"hdrl") + 4)
        while data[movi + 8:movi + 12] != b"movi":
            movi = data.find(b"LIST", movi + 4)
        idx = data.find(b"idx1")
        for i in range(3):
            fourcc, flags, off, size = struct.unpack("<4sIII", data[idx + 8 + 16 * i: idx + 24 + 16 * i])
            self.assertEqual(fourcc, b"00dc")
            self.assertEqual(flags, 0x10)
            chunk = movi + 8 + off                          # 오프셋은 'movi' fourcc 기준
            self.assertEqual(data[chunk:chunk + 4], b"00dc")
            self.assertEqual(struct.unpack("<I", data[chunk + 4:chunk + 8])[0], size)

    def test_zero_frames_still_valid(self):
        AviMjpegWriter(self.path, 64, 48, fps=10).close()
        info = read_avi_info(self.path)
        self.assertEqual(info["frames"], 0)
        self.assertEqual(info["riff_len"], info["file_len"] - 8)


if __name__ == "__main__":
    unittest.main()
