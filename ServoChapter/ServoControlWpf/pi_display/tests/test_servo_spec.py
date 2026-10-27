import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.servo_spec import ServoSpec  # noqa: E402


class ServoSpecTests(unittest.TestCase):
    def test_official_hs311(self):
        s = ServoSpec()
        self.assertAlmostEqual(s.pulse_to_angle(1700), 20.0)
        self.assertAlmostEqual(s.pulse_to_angle(1300), -20.0)
        self.assertAlmostEqual(s.pulse_to_angle(1500), 0.0)
        self.assertEqual(s.angle_to_pulse(20), 1700)
        self.assertEqual(s.angle_to_pulse(-40), 1100)
        self.assertEqual(s.angle_to_pulse(90), 1900)      # 범위 밖은 잘림

    def test_asymmetric_calibration_matches_wpf(self):
        # WPF 문서 12-2 와 같은 수치: 800/1500/2100, ±40
        s = ServoSpec.parse("800,1500,2100,-40,40")
        self.assertAlmostEqual(s.pulse_to_angle(1650), 10.0)
        self.assertAlmostEqual(s.pulse_to_angle(1234), -15.2)
        self.assertEqual(s.angle_to_pulse(15), 1725)

    def test_to_calib_roundtrip_matches_wpf_format(self):
        s = ServoSpec(600, 1500, 2400, -80.0, 80.0)
        self.assertEqual(s.to_calib(), "600,1500,2400,-80,80")              # C# ToCalibString() 과 같은 문자열
        self.assertEqual(ServoSpec.parse(s.to_calib()), s)
        self.assertEqual(ServoSpec(1100, 1500, 1900, -40.5, 40.0).to_calib(), "1100,1500,1900,-40.5,40")

    def test_parse_rejects_bad(self):
        with self.assertRaises(ValueError):
            ServoSpec.parse("1,2,3")
        with self.assertRaises(ValueError):
            ServoSpec.parse("1900,1500,1100,-40,40")


if __name__ == "__main__":
    unittest.main()
