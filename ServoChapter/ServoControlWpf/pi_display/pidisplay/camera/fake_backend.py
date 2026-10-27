"""가짜 카메라: Pi 없이 전체 흐름을 시험하기 위한 테스트 패턴.

PIL(Pillow) 이 있으면 그라데이션 배경 + 시간에 따라 움직이는 막대 + 프레임 번호 + overlay 글(현재 각도)을 그려 JPEG 로 만들고,
없으면 내장된 작은 JPEG 두 장을 번갈아 내보낸다 (화면이 바뀌는 것은 보임).
"""
import base64
import io
import time

from .base import CameraBackend

# PIL 이 없을 때 쓰는 64×48 JPEG 두 장 (Windows 에서 JpegBitmapEncoder 로 만든 것)
_JPEG_A = base64.b64decode(
    "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAwAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDno46tRx0Rx1ajjr0zmCOOrUcdEcdU9Qv/AC829ufn6M4/h9h7/wCfoAGoX/l5t7c/P0Zx/D7D3/z9M2OOiOOrUcdABHHVqOOiOOrUcdAFWOOrUcdEcdU9Qv8Ay829ufn6M4/h9h7/AOfoAGoX/l5t7c/P0Zx/D7D3/wA/TNjjojjq1HHQARx1ajjojjq1HHQARx1T1C/8vNvbn5+jOP4fYe/+foahf+Xm3tz8/RnH8PsPf/P0zY46ANLUL/y829ufn6M4/h9h7/5+mbHHRHHVqOOgAjjq1HHRHHVqOOgAjjqnqF/5ebe3Pz9Gcfw+w9/8/Q1C/wDLzb25+fozj+H2Hv8A5+mbHHQARx1ajjojjq1HHQB//9k=")
_JPEG_B = base64.b64decode(
    "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAwAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDLjjq1HHRHHVqOOuI7wjjq1HHRHHVPUL/y829ufn6M4/h9h7/5+gAahf8Al5t7c/P0Zx/D7D3/AM/TNjjojjq1HHQARx1ajjojjq1HHQBVjjq1HHRHHVPUL/y829ufn6M4/h9h7/5+gAahf+Xm3tz8/RnH8PsPf/P0zY46I46tRx0AEcdWo46I46tRx0AEcdU9Qv8Ay829ufn6M4/h9h7/AOfoahf+Xm3tz8/RnH8PsPf/AD9M2OOgDS1C/wDLzb25+fozj+H2Hv8A5+mbHHRHHVqOOgAjjq1HHRHHVqOOgAjjqnqF/wCXm3tz8/RnH8PsPf8Az9DUL/y829ufn6M4/h9h7/5+mbHHQARx1ajjojjq1HHQB//Z")


class FakeBackend(CameraBackend):
    name = "fake"

    def start(self) -> None:
        try:
            from PIL import Image, ImageDraw  # noqa: F401
            self._pil = True
        except ImportError:
            self._pil = False
            self.width, self.height = 64, 48
        self._frame = 0
        self._run_in_thread(self._loop)

    def _loop(self) -> None:
        period = 1.0 / max(1, self.fps)
        while not self._stop.is_set():
            t0 = time.time()
            self._frame += 1
            self._publish(self._draw() if self._pil else (_JPEG_A if self._frame % 2 else _JPEG_B))
            self._stop.wait(max(0.0, period - (time.time() - t0)))

    def _draw(self) -> bytes:
        from PIL import Image, ImageDraw
        w, h = self.width, self.height
        img = Image.new("RGB", (w, h))
        d = ImageDraw.Draw(img)
        # 배경 그라데이션
        for y in range(0, h, 4):
            c = int(30 + 60 * y / h)
            d.rectangle([0, y, w, y + 4], fill=(c // 2, c, c + 40))
        # 움직이는 막대 (2초 주기)
        x = int((time.time() % 2.0) / 2.0 * w)
        d.rectangle([x - 6, 0, x + 6, h], fill=(255, 200, 40))
        d.rectangle([8, 8, w - 8, 40], outline=(255, 255, 255))
        d.text((16, 14), f"FAKE CAMERA  frame {self._frame}  {time.strftime('%H:%M:%S')}", fill=(255, 255, 255))
        if self.overlay:
            try:
                d.text((16, h - 28), self.overlay(), fill=(255, 255, 255))
            except Exception:
                pass
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=self.quality)
        return buf.getvalue()
