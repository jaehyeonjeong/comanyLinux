"""카메라 백엔드 선택: 실제 백엔드를 전부 실패해도 'auto' 는 자리 표시(none) 로 계속 돈다."""
import os
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import pidisplay.camera as camera  # noqa: E402
from pidisplay.errors import ApiError  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402
from pidisplay.recorder import Recorder  # noqa: E402


def _wait(pred, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if pred():
            return True
        time.sleep(0.02)
    return pred()


class CameraSelectTests(unittest.TestCase):
    def test_auto_falls_back_to_none_when_every_real_backend_fails(self):
        hub = FrameHub()
        # libcamera-vid 는 Pi 가 아니면 명령이 없고, 카메라 없는 Pi 에서는 바로 종료 → 어느 쪽이든 실패
        with mock.patch.object(camera, "ORDER", ["libcamera-vid"]):
            b, tried = camera.select_backend("auto", hub=hub, width=64, height=48, fps=5)
        try:
            self.assertEqual(b.name, "none")
            self.assertEqual(b.record_capable, "none")
            self.assertEqual(b.detail, "카메라 없음")
            self.assertTrue(tried[0].startswith("libcamera-vid: "), tried)
            self.assertIn("자리 표시", tried[-1])
            self.assertTrue(_wait(lambda: hub.seq >= 1), "자리 표시 프레임이 나와야 함")
            jpeg, _ = hub.latest()
            self.assertTrue(jpeg.startswith(b"\xff\xd8"))
            self.assertEqual(hub.backend, "none")
        finally:
            b.stop()

    def test_explicit_backend_still_raises(self):
        with self.assertRaises(RuntimeError) as cm:
            camera.select_backend("libcamera-vid", hub=FrameHub())
        self.assertIn("--camera none", str(cm.exception))

    def test_none_backend_refuses_record_and_snapshot(self):
        hub = FrameHub()
        b, _ = camera.select_backend("none", hub=hub, width=64, height=48)
        tmp = tempfile.mkdtemp()
        try:
            _wait(lambda: hub.seq >= 1)
            rec = Recorder(hub, b, tmp)
            self.assertEqual(rec.status()["kind"], "none")
            with self.assertRaises(ApiError) as cm:
                rec.start()
            self.assertEqual(cm.exception.code, 503)
            with self.assertRaises(ApiError) as cm:
                rec.snapshot()
            self.assertEqual(cm.exception.code, 503)
            self.assertEqual(os.listdir(tmp), [])
        finally:
            b.stop()


if __name__ == "__main__":
    unittest.main()
