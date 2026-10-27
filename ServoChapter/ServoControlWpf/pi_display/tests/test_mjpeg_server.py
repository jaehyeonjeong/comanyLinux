import json
import os
import socket
import sys
import time
import unittest
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.camera.fake_backend import FakeBackend  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402
from pidisplay.mjpeg_server import MjpegServer, ServerContext  # noqa: E402
from pidisplay.movement_log import MovementLog  # noqa: E402
from pidisplay.servo_spec import ServoSpec  # noqa: E402


class MjpegServerTests(unittest.TestCase):
    def setUp(self):
        self.hub = FrameHub()
        self.log = MovementLog(ServoSpec())
        self.cam = FakeBackend(self.hub, width=64, height=48, fps=20)
        self.cam.start()
        self.ctx = ServerContext(self.hub, self.log, None, "1.2.3", 18)
        self.server = MjpegServer(self.ctx, "127.0.0.1", 0)
        self.server.start()
        self.base = f"http://127.0.0.1:{self.server.port}"
        for _ in range(50):
            if self.hub.seq >= 2:
                break
            time.sleep(0.05)

    def tearDown(self):
        self.server.stop()
        self.cam.stop()

    def test_snapshot_is_jpeg(self):
        data = urllib.request.urlopen(self.base + "/snapshot.jpg", timeout=3).read()
        self.assertTrue(data.startswith(b"\xff\xd8"))
        self.assertTrue(data.endswith(b"\xff\xd9"))

    def test_status_json(self):
        self.log.observe(1700, time.time())
        st = json.loads(urllib.request.urlopen(self.base + "/status.json", timeout=3).read().decode("utf-8"))
        self.assertEqual(st["version"], "1.2.3")
        self.assertEqual(st["camera"]["backend"], "fake")
        self.assertEqual(st["servo"]["pulse"], 1700)
        self.assertAlmostEqual(st["servo"]["angle"], 20.0)
        self.assertTrue(st["servo"]["output_on"])
        self.assertEqual(len(st["log"]), 1)

    def test_stream_delivers_two_parts(self):
        s = socket.create_connection(("127.0.0.1", self.server.port), timeout=5)
        s.sendall(b"GET /stream.mjpg HTTP/1.1\r\nHost: x\r\n\r\n")
        buf = b""
        deadline = time.time() + 5
        while buf.count(b"--frame\r\n") < 3 and time.time() < deadline:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
        s.close()
        head, _, rest = buf.partition(b"\r\n\r\n")
        self.assertIn(b"200 OK", head)
        self.assertIn(b"multipart/x-mixed-replace", head)
        self.assertGreaterEqual(rest.count(b"Content-Type: image/jpeg"), 2)
        self.assertIn(b"Content-Length: ", rest)
        # 첫 파트의 Content-Length 만큼 뒤가 정확히 JPEG 인지
        i = rest.find(b"Content-Length: ")
        n = int(rest[i + 16:rest.find(b"\r\n", i)])
        body_start = rest.find(b"\r\n\r\n", i) + 4
        jpeg = rest[body_start:body_start + n]
        self.assertTrue(jpeg.startswith(b"\xff\xd8") and jpeg.endswith(b"\xff\xd9"))
        time.sleep(0.3)
        self.assertEqual(self.ctx.clients, 0)                 # 끊으면 클라이언트 수가 돌아옴

    def test_index_and_log(self):
        html = urllib.request.urlopen(self.base + "/", timeout=3).read().decode("utf-8")
        self.assertIn("stream.mjpg", html)
        txt = urllib.request.urlopen(self.base + "/log.txt", timeout=3).read().decode("utf-8")
        self.assertIsInstance(txt, str)


if __name__ == "__main__":
    unittest.main()
