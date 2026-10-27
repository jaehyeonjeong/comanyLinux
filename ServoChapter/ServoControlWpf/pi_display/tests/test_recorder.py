import os
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.avi_writer import read_avi_info  # noqa: E402
from pidisplay.camera.fake_backend import FakeBackend  # noqa: E402
from pidisplay.errors import ApiError  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402
from pidisplay.recorder import Recorder, hms, human_size, safe_name  # noqa: E402


def _wait(pred, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if pred():
            return True
        time.sleep(0.02)
    return pred()


class RecorderTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.hub = FrameHub()
        self.cam = FakeBackend(self.hub, width=64, height=48, fps=30)
        self.cam.start()
        self.events = []
        _wait(lambda: self.hub.seq >= 2)

    def tearDown(self):
        self.cam.stop()

    def _rec(self, **kw):
        kw.setdefault("on_event", lambda k, t: self.events.append((k, t)))
        return Recorder(self.hub, self.cam, self.tmp, **kw)

    def test_start_stop_writes_avi_and_renames_part(self):
        rec = self._rec()
        st = rec.start()
        self.assertTrue(st["recording"])
        self.assertEqual(st["kind"], "mjpeg-avi")
        self.assertTrue(st["file"].startswith("rec_") and st["file"].endswith(".avi"))
        self.assertTrue(os.path.exists(os.path.join(self.tmp, st["file"] + ".part")))
        _wait(lambda: rec.status()["frames"] >= 3)
        res = rec.stop()
        self.assertFalse(rec.recording)
        self.assertEqual(res["name"], st["file"])
        self.assertGreaterEqual(res["frames"], 3)
        self.assertGreater(res["bytes"], 0)
        self.assertFalse(any(n.endswith(".part") for n in os.listdir(self.tmp)))
        info = read_avi_info(os.path.join(self.tmp, res["name"]))
        self.assertEqual(info["frames"], res["frames"])
        self.assertEqual((info["width"], info["height"]), (64, 48))
        kinds = [k for k, _ in self.events]
        self.assertEqual(kinds, ["rec", "rec"])
        self.assertIn("● 녹화 시작", self.events[0][1])
        self.assertIn("■ 녹화 끝", self.events[1][1])
        lst = rec.list()
        self.assertEqual([d["name"] for d in lst], [res["name"]])
        self.assertEqual(lst[0]["kind"], "video")
        self.assertEqual(rec.status()["last"]["name"], res["name"])

    def test_double_start_and_idle_stop_are_409(self):
        rec = self._rec()
        rec.start()
        with self.assertRaises(ApiError) as cm:
            rec.start()
        self.assertEqual(cm.exception.code, 409)
        rec.stop()
        with self.assertRaises(ApiError) as cm:
            rec.stop()
        self.assertEqual(cm.exception.code, 409)

    def test_custom_name_is_sanitized(self):
        rec = self._rec()
        st = rec.start("../../evil.mp4")
        self.assertEqual(st["file"], "evil.avi")             # 디렉터리 부분 제거, 확장자는 녹화 종류에 맞게
        rec.stop()
        self.assertTrue(os.path.exists(os.path.join(self.tmp, "evil.avi")))

    def test_max_minutes_auto_stop(self):
        now = [1000.0]
        rec = self._rec(max_minutes=1 / 60, clock=lambda: now[0])     # 1 초 제한
        rec.start()
        now[0] += 2.0
        self.assertTrue(_wait(lambda: not rec.recording, 3.0))
        self.assertIn("시간 제한", rec.last_result["reason"])
        self.assertFalse(any(n.endswith(".part") for n in os.listdir(self.tmp)))

    def test_disk_guard_removes_oldest_and_refuses_when_full(self):
        # 2 KB 짜리 옛 파일 3개, 상한 2.5 KB → 시작 전에 가장 오래된 것부터 지워 상한 아래로
        for i in range(3):
            p = os.path.join(self.tmp, f"rec_old{i}.avi")
            with open(p, "wb") as f:
                f.write(b"x" * 2048)
            os.utime(p, (1_600_000_000 + i, 1_600_000_000 + i))
        rec = self._rec(max_gb=2560 / (1024 ** 3))
        rec.start()
        rec.stop()
        names = sorted(rec._files())
        self.assertNotIn("rec_old0.avi", names)
        self.assertNotIn("rec_old1.avi", names)
        self.assertIn("rec_old2.avi", names)
        self.assertTrue(any("디스크 정리" in t for _, t in self.events))
        rec2 = self._rec(min_free_mb=10 ** 9)                # 지구상 어떤 디스크도 모자람
        with self.assertRaises(ApiError) as cm:
            rec2.start()
        self.assertEqual(cm.exception.code, 507)
        self.assertFalse(rec2.recording)

    def test_snapshot_falls_back_to_preview_frame(self):
        rec = self._rec()
        res = rec.snapshot()
        self.assertTrue(res["name"].startswith("img_") and res["name"].endswith(".jpg"))
        self.assertEqual((res["width"], res["height"]), (64, 48))
        self.assertIn("미리보기", res["note"])
        with open(os.path.join(self.tmp, res["name"]), "rb") as f:
            data = f.read()
        self.assertTrue(data.startswith(b"\xff\xd8"))
        self.assertEqual(rec.list()[0]["kind"], "image")

    def test_path_of_rejects_escape_and_missing(self):
        rec = self._rec()
        for bad in ("../x.avi", "x.txt", "", "..", "a/b.mp4"):
            with self.assertRaises(ApiError):
                rec.path_of(bad)
        with self.assertRaises(ApiError) as cm:
            rec.path_of("nope.avi")
        self.assertEqual(cm.exception.code, 404)
        self.assertEqual(safe_name("sub/dir/ok.mp4"), "ok.mp4")

    def test_helpers(self):
        self.assertEqual(hms(3723), "01:02:03")
        self.assertEqual(human_size(12 * 1024 * 1024), "12.0 MB")
        self.assertEqual(human_size(3 * 1024 ** 3), "3.00 GB")


if __name__ == "__main__":
    unittest.main()
