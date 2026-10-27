import os
import sys
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.camera.fake_backend import _JPEG_A  # noqa: E402
from pidisplay.detector import Detection, Detector, FakeEngine  # noqa: E402
from pidisplay.errors import ApiError  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402


def _wait(pred, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if pred():
            return True
        time.sleep(0.02)
    return pred()


class DetectorTests(unittest.TestCase):
    def test_fake_engine_flow_through_hub_filter(self):
        eng = FakeEngine()
        det = Detector(every=2, engine=eng, enabled=True)
        hub = FrameHub()
        hub.set_filter(det.annotate)
        for _ in range(4):
            hub.publish(_JPEG_A, 64, 48)
        self.assertTrue(_wait(lambda: eng.detect_calls >= 1))
        self.assertTrue(_wait(lambda: len(det.latest) == 2))
        hub.publish(_JPEG_A, 64, 48)                       # 결과가 있으니 이번엔 그린다
        self.assertGreaterEqual(eng.draw_calls, 1)
        st = det.status()
        self.assertTrue(st["available"] and st["enabled"])
        self.assertEqual(st["model"], "fake")
        self.assertEqual([o["label_ko"] for o in st["objects"]], ["사람", "의자"])
        self.assertEqual(st["summary"], "사람 1 · 의자 1")
        self.assertEqual(st["objects"][0]["box"], [0.1, 0.1, 0.3, 0.6])
        det.set_enabled(False)
        self.assertEqual(det.latest, [])
        self.assertEqual(det.summary(), "인식 꺼짐")
        n = eng.draw_calls
        self.assertIs(hub.publish(_JPEG_A, 64, 48), None)
        self.assertEqual(eng.draw_calls, n)                  # 꺼지면 손대지 않음
        det.stop()

    def test_configure_and_summary_counts(self):
        eng = FakeEngine([Detection("person", 0.8, (0, 0, 0.5, 0.5)), Detection("person", 0.6, (0.5, 0.5, 0.4, 0.4)),
                          Detection("dog", 0.7, (0.2, 0.2, 0.2, 0.2))])
        det = Detector(every=1, engine=eng, enabled=True)
        det.configure(every=5, conf=2.0)
        self.assertEqual((det.every, det.conf), (5, 0.99))
        for _ in range(5):
            det.annotate(_JPEG_A)
        self.assertTrue(_wait(lambda: len(det.latest) == 3))
        self.assertEqual(det.summary(), "사람 2 · 개 1")
        det.stop()

    def test_unavailable_without_opencv(self):
        try:
            import cv2  # noqa: F401
            self.skipTest("OpenCV 가 있는 환경")
        except ImportError:
            pass
        det = Detector()
        self.assertFalse(det.available)
        self.assertIn("OpenCV", det.error)
        self.assertEqual(det.annotate(_JPEG_A), _JPEG_A)
        with self.assertRaises(ApiError) as cm:
            det.set_enabled(True)
        self.assertEqual(cm.exception.code, 501)
        self.assertEqual(det.summary(), "인식 불가 (OpenCV 없음)")
        self.assertFalse(det.status()["enabled"])


if __name__ == "__main__":
    unittest.main()
