"""서보 사양과 펄스폭 ↔ 각도 환산. WPF 의 Model/ServoSpec.cs 와 같은 공식(중립 기준 양쪽 따로 비례).

HS-311 공식값: 1100 / 1500 / 1900 µs, −40° ~ +40° (10 µs/°)
"""
from dataclasses import dataclass


@dataclass(frozen=True)
class ServoSpec:
    min_pulse: int = 1100
    neutral_pulse: int = 1500
    max_pulse: int = 1900
    min_angle: float = -40.0
    max_angle: float = 40.0

    def pulse_to_angle(self, pulse: int) -> float:
        if pulse >= self.neutral_pulse:
            return (pulse - self.neutral_pulse) * self.max_angle / float(self.max_pulse - self.neutral_pulse)
        return -(self.neutral_pulse - pulse) * -self.min_angle / float(self.neutral_pulse - self.min_pulse)

    def angle_to_pulse(self, angle: float) -> int:
        a = max(self.min_angle, min(self.max_angle, angle))
        if a >= 0:
            return int(round(self.neutral_pulse + a * (self.max_pulse - self.neutral_pulse) / self.max_angle))
        return int(round(self.neutral_pulse + a * (self.neutral_pulse - self.min_pulse) / -self.min_angle))

    def is_valid(self) -> bool:
        return (500 <= self.min_pulse < self.neutral_pulse < self.max_pulse <= 2500
                and -90 <= self.min_angle < 0 < self.max_angle <= 90)

    @staticmethod
    def parse(text: str) -> "ServoSpec":
        """'1100,1500,1900,-40,40' 형식 (WPF 의 보정값과 같게 맞출 때 사용)"""
        parts = [p.strip() for p in text.split(",")]
        if len(parts) != 5:
            raise ValueError("--calib 는 '최소펄스,중립펄스,최대펄스,최소각,최대각' 다섯 값이어야 합니다")
        spec = ServoSpec(int(parts[0]), int(parts[1]), int(parts[2]), float(parts[3]), float(parts[4]))
        if not spec.is_valid():
            raise ValueError("보정값이 올바르지 않습니다 (500~2500 µs, 최소<중립<최대, 최소각<0<최대각)")
        return spec

    def __str__(self) -> str:
        return f"{self.min_pulse}~{self.neutral_pulse}~{self.max_pulse} µs, {self.min_angle:g}°~{self.max_angle:g}°"
