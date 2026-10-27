import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.camera.fake_backend import FakeBackend  # noqa: E402
from pidisplay.camera_settings import AWB_ENUM, CameraManager, CameraSettings  # noqa: E402
from pidisplay.errors import ApiError  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402


class CameraSettingsTests(unittest.TestCase):
    def test_partial_update_clamps_and_validates(self):
        s = CameraSettings.from_dict({"brightness": 5, "contrast": -1, "awb": "daylight", "hflip": "true", "junk": 1})
        self.assertEqual((s.brightness, s.contrast, s.awb, s.hflip, s.vflip), (1.0, 0.0, "daylight", True, False))
        self.assertEqual(s.record_mode, "1080p30")           # 건드리지 않은 값은 유지
        with self.assertRaises(ApiError):
            CameraSettings.from_dict({"record_mode": "4k"})
        with self.assertRaises(ApiError):
            CameraSettings.from_dict({"awb": "purple"})
        with self.assertRaises(ApiError):
            CameraSettings.from_dict([1, 2])

    def test_needs_restart_only_for_mode_and_flip(self):
        base = CameraSettings()
        self.assertFalse(base.needs_restart(CameraSettings.from_dict({"brightness": 0.3}, base)))
        self.assertTrue(base.needs_restart(CameraSettings.from_dict({"record_mode": "720p60"}, base)))
        self.assertTrue(base.needs_restart(CameraSettings.from_dict({"vflip": True}, base)))

    def test_controls_mapping_and_sizes(self):
        s = CameraSettings(brightness=0.2, contrast=1.5, saturation=0.5, awb="cloudy", record_mode="480p90")
        c = s.controls()
        self.assertEqual(c, {"Brightness": 0.2, "Contrast": 1.5, "Saturation": 0.5, "AwbMode": AWB_ENUM["cloudy"]})
        self.assertEqual(s.record_size(), (640, 480, 90))

    def test_save_and_load_roundtrip(self):
        tmp = tempfile.mkdtemp()
        path = os.path.join(tmp, "sub", "camera.json")
        self.assertEqual(CameraSettings.load(path), CameraSettings())          # 없으면 기본값
        s = CameraSettings(record_mode="720p60", hflip=True, brightness=-0.25, awb="tungsten")
        s.save(path)
        self.assertEqual(CameraSettings.load(path), s)
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
        self.assertEqual(raw["record_mode"], "720p60")
        with open(path, "w") as f:
            f.write("{broken")
        self.assertEqual(CameraSettings.load(path), CameraSettings())          # 깨진 파일 → 기본값


class CameraManagerTests(unittest.TestCase):
    def setUp(self):
        self.hub = FrameHub()
        self.tmp = tempfile.mkdtemp()
        self.made = []

        def select(name, **kw):
            b = FakeBackend(**kw)
            b.start()
            self.made.append(b)
            return b, [f"{name}: 사용"]
        self.busy = False
        self.mgr = CameraManager(select, "fake", dict(hub=self.hub, width=64, height=48, fps=20),
                                 CameraSettings(), os.path.join(self.tmp, "camera.json"), is_busy=lambda: self.busy)
        self.mgr.start()

    def tearDown(self):
        self.mgr.stop()

    def test_start_passes_settings_to_backend(self):
        b = self.mgr.backend
        self.assertEqual(b.record_mode, "1080p30")
        self.assertEqual(b.controls["AwbMode"], 0)
        d = self.mgr.describe()
        self.assertEqual(d["record_size"], [1920, 1080])
        self.assertEqual(d["still_size"], [2592, 1944])
        self.assertEqual(d["record_capable"], "mjpeg-avi")

    def test_apply_immediate_vs_restart(self):
        r = self.mgr.apply({"brightness": 0.5})
        self.assertFalse(r["restarted"])
        self.assertEqual(len(self.made), 1)                   # 다시 열지 않음
        self.assertEqual(self.mgr.backend.controls["Brightness"], 0.5)
        r = self.mgr.apply({"record_mode": "720p60", "vflip": True})
        self.assertTrue(r["restarted"])
        self.assertEqual(len(self.made), 2)                   # 새 백엔드
        self.assertTrue(self.made[0]._stop.is_set())          # 옛 백엔드는 멈춤
        self.assertEqual(self.mgr.backend.record_mode, "720p60")
        self.assertTrue(self.mgr.backend.vflip)
        saved = CameraSettings.load(os.path.join(self.tmp, "camera.json"))
        self.assertEqual((saved.record_mode, saved.vflip, saved.brightness), ("720p60", True, 0.5))

    def test_restart_refused_while_busy(self):
        self.busy = True
        with self.assertRaises(ApiError) as cm:
            self.mgr.apply({"hflip": True})
        self.assertEqual(cm.exception.code, 409)
        self.assertFalse(self.mgr.settings.hflip)
        self.mgr.apply({"contrast": 1.2})                      # 즉시 적용 값은 녹화 중에도 OK
        self.assertEqual(self.mgr.settings.contrast, 1.2)


if __name__ == "__main__":
    unittest.main()
