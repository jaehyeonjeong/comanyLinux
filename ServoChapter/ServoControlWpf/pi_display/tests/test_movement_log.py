import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.movement_log import MovementLog  # noqa: E402
from pidisplay.servo_spec import ServoSpec  # noqa: E402


class MovementLogTests(unittest.TestCase):
    def test_records_only_changes(self):
        log = MovementLog(ServoSpec())
        self.assertEqual(log.observe(1500, 1.0).kind, "start")
        self.assertIsNone(log.observe(1500, 1.05))          # 같은 값 → 기록 없음
        e = log.observe(1700, 1.1)
        self.assertEqual((e.kind, e.pulse), ("move", 1700))
        self.assertAlmostEqual(e.angle, 20.0)
        self.assertAlmostEqual(e.delta, 20.0)
        self.assertEqual(log.observe(0, 1.2).kind, "off")
        self.assertAlmostEqual(log.current_angle, 20.0)      # 꺼져도 마지막 각도 유지
        on = log.observe(1300, 1.3)
        self.assertEqual(on.kind, "on")
        self.assertAlmostEqual(on.angle, -20.0)
        self.assertEqual(log.total, 4)
        self.assertEqual(len(log.recent(10)), 4)

    def test_unknown_then_known(self):
        log = MovementLog(ServoSpec())
        self.assertEqual(log.observe(None, 1.0).kind, "unknown")
        self.assertIsNone(log.observe(None, 1.1))
        self.assertEqual(log.observe(1600, 1.2).kind, "on")     # 알 수 없음 → 펄스 생김 = 켜짐
        self.assertIsNone(log.recent(1)[0].delta)
        self.assertEqual(log.observe(1700, 1.3).kind, "move")

    def test_pending_note_attaches_within_ttl(self):
        log = MovementLog(ServoSpec())
        log.observe(1500, 10.0)
        log.set_pending_note("WPF +20.0°", ttl=1.0, now=10.0)
        e = log.observe(1700, 10.5)                           # 1초 안 → 붙음
        self.assertEqual(e.note, "WPF +20.0°")
        e2 = log.observe(1800, 10.6)                          # 한 번 쓰면 사라짐
        self.assertEqual(e2.note, "")
        log.set_pending_note("늦은 메모", ttl=1.0, now=18.0)
        self.assertEqual(log.observe(1900, 20.0).note, "")    # 만료 (18+1 < 20)

    def test_file_append_and_text(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "sub", "moves.log")
            log = MovementLog(ServoSpec(), path, version="9.9.9")
            log.observe(1500, 1000.0)
            log.observe(1700, 1001.0)
            with open(path, encoding="utf-8") as f:
                lines = f.read().splitlines()
            self.assertIn("pi_display v9.9.9 시작", lines[0])
            self.assertIn("1700 µs", lines[2])
            self.assertIn("Δ+20.0°", lines[2])
            text = log.recent(1)[0].text()
            self.assertRegex(text, r"^\d\d:\d\d:\d\d\.\d{3}  1700 µs  \+20\.0°  Δ\+20\.0°$")


if __name__ == "__main__":
    unittest.main()
