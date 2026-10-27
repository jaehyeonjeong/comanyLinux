import json
import os
import socket
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pidisplay.avi_writer import read_avi_info  # noqa: E402
from pidisplay.camera.fake_backend import FakeBackend  # noqa: E402
from pidisplay.camera_settings import CameraManager, CameraSettings  # noqa: E402
from pidisplay.detector import Detector, FakeEngine  # noqa: E402
from pidisplay.frame_hub import FrameHub  # noqa: E402
from pidisplay.mjpeg_server import MjpegServer, ServerContext  # noqa: E402
from pidisplay.movement_log import MovementLog  # noqa: E402
from pidisplay.recorder import Recorder  # noqa: E402
from pidisplay.servo_spec import ServoSpec  # noqa: E402


def _wait(pred, timeout=3.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if pred():
            return True
        time.sleep(0.02)
    return pred()


class MjpegServerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.hub = FrameHub()
        self.log = MovementLog(ServoSpec())

        def select(name, **kw):
            b = FakeBackend(**kw)
            b.start()
            return b, [f"{name}: 사용"]
        self.recorder = None
        self.camera = CameraManager(select, "fake", dict(hub=self.hub, width=64, height=48, fps=20), CameraSettings(),
                                    os.path.join(self.tmp, "camera.json"), is_busy=lambda: bool(self.recorder and self.recorder.recording))
        self.camera.start()
        self.cam = self.camera.backend

        class _Proxy:
            def __getattr__(_, item):
                return getattr(self.camera.backend, item)
        self.recorder = Recorder(self.hub, _Proxy(), self.tmp, on_event=lambda k, t: self.log.event(t, k))
        self.engine = FakeEngine()
        self.detector = Detector(every=1, engine=self.engine)
        self.hub.set_filter(self.detector.annotate)
        self.ctx = ServerContext(self.hub, self.log, None, "1.2.3", 18,
                                 recorder=self.recorder, detector=self.detector, camera=self.camera)
        self.server = MjpegServer(self.ctx, "127.0.0.1", 0)
        self.server.start()
        self.base = f"http://127.0.0.1:{self.server.port}"
        _wait(lambda: self.hub.seq >= 2)

    def tearDown(self):
        self.server.stop()
        self.detector.stop()
        self.camera.stop()

    # ── 도우미 ──
    def _get(self, path, timeout=3):
        return urllib.request.urlopen(self.base + path, timeout=timeout)

    def _json(self, path):
        return json.loads(self._get(path).read().decode("utf-8"))

    def _post(self, path, body=None):
        data = json.dumps(body).encode("utf-8") if body is not None else b""
        req = urllib.request.Request(self.base + path, data=data, method="POST",
                                     headers={"Content-Type": "application/json"} if body is not None else {})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as ex:
            return ex.code, json.loads(ex.read().decode("utf-8"))

    # ── 기존 기능 ──
    def test_snapshot_is_jpeg(self):
        data = self._get("/snapshot.jpg").read()
        self.assertTrue(data.startswith(b"\xff\xd8"))
        self.assertTrue(data.endswith(b"\xff\xd9"))

    def test_status_json(self):
        self.log.observe(1700, time.time())
        st = self._json("/status.json")
        self.assertEqual(st["version"], "1.2.3")
        self.assertEqual(st["camera"]["backend"], "fake")
        self.assertEqual(st["servo"]["pulse"], 1700)
        self.assertAlmostEqual(st["servo"]["angle"], 20.0)
        self.assertTrue(st["servo"]["output_on"])
        self.assertEqual(len(st["log"]), 1)
        # v1.3.0 — 녹화·인식·설정 블록
        self.assertFalse(st["record"]["recording"])
        self.assertEqual(st["record"]["kind"], "mjpeg-avi")
        self.assertIn("free_mb", st["record"]["disk"])
        self.assertFalse(st["detect"]["enabled"])
        self.assertEqual(st["settings"]["settings"]["record_mode"], "1080p30")
        self.assertEqual(st["settings"]["record_modes"], ["1080p30", "720p60", "480p90"])

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
        i = rest.find(b"Content-Length: ")
        n = int(rest[i + 16:rest.find(b"\r\n", i)])
        body_start = rest.find(b"\r\n\r\n", i) + 4
        jpeg = rest[body_start:body_start + n]
        self.assertTrue(jpeg.startswith(b"\xff\xd8") and jpeg.endswith(b"\xff\xd9"))
        time.sleep(0.3)
        self.assertEqual(self.ctx.clients, 0)

    def test_index_and_log(self):
        html = self._get("/").read().decode("utf-8")
        self.assertIn("stream.mjpg", html)
        self.assertIn("recordings.json", html)
        txt = self._get("/log.txt").read().decode("utf-8")
        self.assertIsInstance(txt, str)

    # ── 녹화 ──
    def test_record_start_stop_list_download(self):
        code, st = self._post("/record/start")
        self.assertEqual(code, 200)
        self.assertTrue(st["recording"])
        name = st["file"]
        self.assertTrue(name.endswith(".avi"))
        self.assertTrue(self._json("/record/status")["recording"])
        self.assertTrue(self._json("/status.json")["record"]["recording"])
        self.assertEqual(self._post("/record/start")[0], 409)
        _wait(lambda: self.recorder.status()["frames"] >= 3)
        code, res = self._post("/record/stop")
        self.assertEqual(code, 200)
        self.assertEqual(res["name"], name)
        self.assertGreaterEqual(res["frames"], 3)
        self.assertEqual(self._post("/record/stop")[0], 409)
        lst = self._json("/recordings.json")
        self.assertEqual([d["name"] for d in lst], [name])
        self.assertEqual(lst[0]["bytes"], res["bytes"])
        data = self._get("/recordings/" + name).read()
        self.assertEqual(len(data), res["bytes"])
        self.assertTrue(data.startswith(b"RIFF"))
        self.assertEqual(read_avi_info(os.path.join(self.tmp, name))["frames"], res["frames"])
        self.assertFalse(any(n.endswith(".part") for n in os.listdir(self.tmp)))
        # 로그에도 남음
        texts = [e.text() for e in self.log.recent(10)]
        self.assertTrue(any("● 녹화 시작" in t for t in texts))
        self.assertTrue(any("■ 녹화 끝" in t for t in texts))
        self.assertEqual(self.log.recent(1)[0].kind, "rec")

    def test_record_named_via_query(self):
        code, st = self._post("/record/start?name=test_clip")
        self.assertEqual(code, 200)
        self.assertEqual(st["file"], "test_clip.avi")
        self._post("/record/stop")

    def test_download_rejects_bad_names(self):
        for bad in ("/recordings/..%2F..%2Fetc%2Fpasswd", "/recordings/x.txt", "/recordings/"):
            try:
                self._get(bad)
                self.fail("should fail: " + bad)
            except urllib.error.HTTPError as ex:
                self.assertIn(ex.code, (400, 404))
                self.assertIn("error", json.loads(ex.read().decode("utf-8")))

    def test_snapshot_save(self):
        code, res = self._post("/snapshot/save")
        self.assertEqual(code, 200)
        self.assertTrue(res["name"].startswith("img_") and res["name"].endswith(".jpg"))
        self.assertEqual((res["width"], res["height"]), (64, 48))
        self.assertTrue(os.path.exists(os.path.join(self.tmp, res["name"])))
        self.assertEqual(self._json("/recordings.json")[0]["kind"], "image")

    # ── 카메라 설정 ──
    def test_camera_settings_get_post(self):
        d = self._json("/camera/settings")
        self.assertEqual(d["settings"]["record_mode"], "1080p30")
        self.assertEqual(d["record_size"], [1920, 1080])
        self.assertIn("auto", d["awb_modes"])
        code, r = self._post("/camera/settings", {"brightness": 0.5, "awb": "daylight"})
        self.assertEqual(code, 200)
        self.assertFalse(r["restarted"])
        self.assertEqual((r["settings"]["brightness"], r["settings"]["awb"]), (0.5, "daylight"))
        code, r = self._post("/camera/settings", {"record_mode": "720p60"})
        self.assertEqual(code, 200)
        self.assertTrue(r["restarted"])
        self.assertEqual(self.camera.restarts, 1)
        self.assertEqual(self.camera.backend.record_mode, "720p60")
        with open(os.path.join(self.tmp, "camera.json"), encoding="utf-8") as f:
            saved = json.load(f)
        self.assertEqual((saved["record_mode"], saved["brightness"]), ("720p60", 0.5))
        self.assertEqual(self._post("/camera/settings", {"record_mode": "8k"})[0], 400)
        self.assertEqual(self._post("/camera/settings?vflip=1")[0], 200)     # 쿼리로도
        self.assertTrue(self.camera.settings.vflip)
        _wait(lambda: self.hub.seq >= 2)

    def test_camera_restart_refused_while_recording(self):
        self._post("/record/start")
        code, r = self._post("/camera/settings", {"hflip": True})
        self.assertEqual(code, 409)
        self.assertIn("녹화", r["error"])
        self.assertEqual(self._post("/camera/settings", {"contrast": 1.3})[0], 200)   # 즉시 적용 값은 OK
        self._post("/record/stop")

    # ── 인식 ──
    def test_detect_toggle_and_status(self):
        st = self._json("/detect/status")
        self.assertTrue(st["available"])
        self.assertFalse(st["enabled"])
        code, st = self._post("/detect/on")
        self.assertEqual(code, 200)
        self.assertTrue(st["enabled"])
        self.assertTrue(_wait(lambda: self._json("/detect/status")["objects"]))
        st = self._json("/status.json")["detect"]
        self.assertEqual(st["summary"], "사람 1 · 의자 1")
        self.assertEqual(st["objects"][0]["label"], "person")
        code, st = self._post("/detect/config?every=4&conf=0.3")
        self.assertEqual((code, st["every"], st["conf"]), (200, 4, 0.3))
        self.assertEqual(self._post("/detect/config?every=x")[0], 400)
        code, st = self._post("/detect/off")
        self.assertFalse(st["enabled"])
        self.assertEqual(st["objects"], [])

    # ── 오류 모양 ──
    def test_errors_are_json(self):
        self.assertEqual(self._post("/nope")[0], 404)
        req = urllib.request.Request(self.base + "/camera/settings", data=b"{bad", method="POST")
        with self.assertRaises(urllib.error.HTTPError) as cm:
            urllib.request.urlopen(req, timeout=3)
        self.assertEqual(cm.exception.code, 400)
        self.assertIn("JSON", json.loads(cm.exception.read().decode("utf-8"))["error"])

    # ── 서보 보정값 ──
    def test_servo_calib_get_post_changes_angles(self):
        d = self._json("/servo/calib")
        self.assertEqual(d["calib"], "1100,1500,1900,-40,40")
        self.log.observe(600, time.time())                                   # 600 µs: 공식값 기준 −90°
        self.assertAlmostEqual(self._json("/status.json")["servo"]["angle"], -90.0)
        saved = []
        self.ctx.on_calib = saved.append
        code, r = self._post("/servo/calib", {"calib": "600,1500,2400,-80,80"})
        self.assertEqual(code, 200)
        self.assertTrue(r["changed"])
        self.assertEqual(r["calib"], "600,1500,2400,-80,80")
        self.assertEqual(len(saved), 1)
        st = self._json("/status.json")["servo"]
        self.assertEqual(st["calib"], "600,1500,2400,-80,80")
        self.assertAlmostEqual(st["angle"], -80.0)                           # 같은 펄스, WPF 보정값으로 다시 계산
        texts = [e.text() for e in self.log.recent(3)]
        self.assertTrue(any("보정값 변경" in t and "-80.0°" in t for t in texts))
        code, r = self._post("/servo/calib", {"calib": "600,1500,2400,-80,80"})   # 같은 값 → 바뀐 것 없음
        self.assertFalse(r["changed"])
        self.assertEqual(len(saved), 1)
        self.assertEqual(self._post("/servo/calib", {"calib": "1,2,3"})[0], 400)
        self.assertEqual(self._post("/servo/calib", {"calib": "2000,1500,1900,-40,40"})[0], 400)
        self.assertEqual(self._post("/servo/calib", {})[0], 400)

    def test_features_absent_give_501(self):
        ctx = ServerContext(self.hub, self.log, None, "1.2.3", 18)
        srv = MjpegServer(ctx, "127.0.0.1", 0)
        srv.start()
        try:
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{srv.port}/record/status", timeout=3)
                self.fail()
            except urllib.error.HTTPError as ex:
                self.assertEqual(ex.code, 501)
            st = json.loads(urllib.request.urlopen(f"http://127.0.0.1:{srv.port}/status.json", timeout=3).read())
            self.assertNotIn("record", st)
        finally:
            srv.stop()


if __name__ == "__main__":
    unittest.main()
