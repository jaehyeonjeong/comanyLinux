using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ServoControlWpf.Services;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 가짜 pi_display API 서버 (TcpListener). 요청 줄(메서드·경로)과 본문을 기록하고, 경로별로 정해 둔 JSON·상태 코드를 돌려준다.
/// 기본 응답은 pi_display/pidisplay/mjpeg_server.py 가 주는 모양을 그대로 흉내 낸다.
/// </summary>
internal sealed class FakePiApiServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public readonly List<(string method, string path, string body)> Requests = new();
    public readonly Dictionary<string, (int code, string json)> Routes = new();
    public volatile bool Recording;
    public volatile bool DetectOn;
    /// <summary>Pi 화면 프로그램이 쓰는 보정값 (POST /servo/calib 로 바뀜)</summary>
    public volatile string Calib = "1100,1500,1900,-40,40";
    public int Delay { get; set; }        // 응답 지연(ms) — 시간 초과 시험용

    public FakePiApiServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string StreamUrl => $"http://127.0.0.1:{Port}/stream.mjpg";
    public string Base => $"http://127.0.0.1:{Port}";

    public (string method, string path, string body)[] Snapshot() { lock (Requests) return Requests.ToArray(); }

    private (int code, string json) Respond(string method, string path, string body)
    {
        if (Routes.TryGetValue(path, out var fixedResp)) return fixedResp;
        switch (method, path)
        {
            case ("GET", "/status.json"):
            {
                var rec = Recording ? "true" : "false";
                var file = Recording ? "\"rec_x.avi\"" : "null";
                var sec = Recording ? "5.2" : "0";
                var bytes = Recording ? "123456" : "0";
                var det = DetectOn ? "true" : "false";
                return (200, "{\"version\":\"9.9.9\",\"camera\":{\"backend\":\"fake\",\"width\":64,\"height\":48,\"fps\":9.8,\"frames\":100,\"clients\":1}," +
                    $"\"servo\":{{\"gpio\":18,\"pulse\":1500,\"angle\":0.0,\"output_on\":true,\"connected\":true,\"backend\":\"socket\",\"polls\":10,\"calib\":\"{Calib}\"}},\"log\":[]," +
                    $"\"record\":{{\"recording\":{rec},\"kind\":\"mjpeg-avi\",\"file\":{file},\"seconds\":{sec},\"bytes\":{bytes},\"frames\":52,\"dir\":\"/home/user/Videos/pi_display\"," +
                    "\"max_minutes\":60,\"last\":null,\"disk\":{\"free_mb\":24000,\"used_mb\":12,\"max_gb\":8.0}}," +
                    $"\"detect\":{{\"available\":true,\"enabled\":{det},\"model\":\"fake\",\"error\":\"\",\"every\":3,\"conf\":0.5,\"fps\":4.1,\"infer_ms\":120," +
                    "\"objects\":[{\"label\":\"person\",\"label_ko\":\"사람\",\"conf\":0.9,\"box\":[0.1,0.1,0.3,0.6]}],\"summary\":\"사람 1\"}," +
                    "\"settings\":{\"settings\":{\"record_mode\":\"720p60\",\"hflip\":true,\"vflip\":false,\"brightness\":0.25,\"contrast\":1.2,\"saturation\":0.8,\"awb\":\"daylight\"}," +
                    "\"record_modes\":[\"1080p30\",\"720p60\",\"480p90\"],\"awb_modes\":[\"auto\",\"daylight\",\"cloudy\",\"tungsten\",\"fluorescent\",\"indoor\"]," +
                    "\"record_size\":[1280,720],\"record_fps\":60,\"still_size\":[2592,1944],\"record_capable\":\"mjpeg-avi\",\"controls_supported\":false,\"backend_detail\":\"\",\"restarts\":0}}");
            }
            case ("POST", "/record/start"):
                if (Recording) return (409, """{"error":"이미 녹화 중입니다"}""");
                Recording = true;
                return (200, """{"recording":true,"kind":"mjpeg-avi","file":"rec_x.avi","seconds":0.0,"bytes":0,"frames":0,"dir":"/home/user/Videos/pi_display","max_minutes":60,"last":null,"disk":{"free_mb":24000,"used_mb":12,"max_gb":8.0}}""");
            case ("POST", "/record/stop"):
                if (!Recording) return (409, """{"error":"녹화 중이 아닙니다"}""");
                Recording = false;
                return (200, """{"name":"rec_x.avi","bytes":2097152,"seconds":5.2,"frames":52,"kind":"mjpeg-avi","reason":""}""");
            case ("GET", "/record/status"):
                return (200, $$"""{"recording":{{(Recording ? "true" : "false")}},"kind":"mjpeg-avi","file":null,"seconds":0,"bytes":0,"frames":0,"dir":"/x","max_minutes":60,"last":null,"disk":{"free_mb":1,"used_mb":0,"max_gb":8} }""");
            case ("GET", "/recordings.json"):
                return (200, """[{"name":"rec_x.avi","bytes":2097152,"mtime":1761500000,"kind":"video"},{"name":"img_y.jpg","bytes":4096,"mtime":1761500001,"kind":"image"}]""");
            case ("POST", "/snapshot/save"):
                return (200, """{"name":"img_20261027_120000.jpg","width":2592,"height":1944,"bytes":1048576,"note":""}""");
            case ("GET", "/camera/settings"):
                return (200, """{"settings":{"record_mode":"1080p30","hflip":false,"vflip":false,"brightness":0.0,"contrast":1.0,"saturation":1.0,"awb":"auto"},"record_modes":["1080p30","720p60","480p90"],"awb_modes":["auto","daylight"],"record_size":[1920,1080],"record_fps":30,"still_size":[2592,1944],"record_capable":"h264","controls_supported":true,"backend_detail":"main 1920×1080@30 + lores MJPEG(HW)","restarts":0}""");
            case ("POST", "/camera/settings"):
            {
                using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                var mode = doc.RootElement.TryGetProperty("record_mode", out var m) ? m.GetString() : "1080p30";
                var restarted = doc.RootElement.TryGetProperty("record_mode", out _) || doc.RootElement.TryGetProperty("hflip", out _) || doc.RootElement.TryGetProperty("vflip", out _);
                var bright = doc.RootElement.TryGetProperty("brightness", out var b) ? b.GetDouble() : 0.0;
                return (200, $$"""{"settings":{"record_mode":"{{mode}}","hflip":false,"vflip":false,"brightness":{{bright.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"contrast":1.0,"saturation":1.0,"awb":"auto"},"restarted":{{(restarted ? "true" : "false")}},"applied":true }""");
            }
            case ("POST", "/detect/on"):
                DetectOn = true;
                return (200, """{"available":true,"enabled":true,"model":"fake","error":"","every":3,"conf":0.5,"fps":0,"infer_ms":0,"objects":[],"summary":"감지된 것 없음"}""");
            case ("POST", "/detect/off"):
                DetectOn = false;
                return (200, """{"available":true,"enabled":false,"model":"fake","error":"","every":3,"conf":0.5,"fps":0,"infer_ms":0,"objects":[],"summary":"인식 꺼짐"}""");
            case ("POST", "/servo/calib"):
            {
                using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                if (!doc.RootElement.TryGetProperty("calib", out var c)) return (400, """{"error":"calib 값이 없습니다"}""");
                Calib = c.GetString() ?? "";
                return (200, $$"""{"calib":"{{Calib}}","text":"{{Calib.Replace(",", "~")}}","changed":true}""");
            }
            case ("GET", "/detect/status"):
                return (200, """{"available":false,"enabled":false,"model":"-","error":"OpenCV 없음","every":3,"conf":0.5,"fps":0,"infer_ms":0,"objects":[],"summary":"인식 불가 (OpenCV 없음)"}""");
            default:
                return (404, """{"error":"not found"}""");
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception) { }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                var s = client.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    // 헤더
                    var buf = new List<byte>();
                    var headerEnd = -1;
                    var one = new byte[1];
                    while (headerEnd < 0)
                    {
                        var n = await s.ReadAsync(one, _cts.Token);
                        if (n <= 0) return;
                        buf.Add(one[0]);
                        if (buf.Count >= 4 && buf[^4] == '\r' && buf[^3] == '\n' && buf[^2] == '\r' && buf[^1] == '\n') headerEnd = buf.Count;
                    }
                    var head = Encoding.UTF8.GetString(buf.ToArray());
                    var lines = head.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var method = parts[0];
                    var path = parts[1].Split('?')[0];
                    var len = 0;
                    foreach (var l in lines)
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(l[15..].Trim());
                    var bodyBytes = new byte[len];
                    var got = 0;
                    while (got < len)
                    {
                        var n = await s.ReadAsync(bodyBytes.AsMemory(got), _cts.Token);
                        if (n <= 0) return;
                        got += n;
                    }
                    var body = Encoding.UTF8.GetString(bodyBytes);
                    lock (Requests) Requests.Add((method, path, body));
                    if (Delay > 0) await Task.Delay(Delay, _cts.Token);
                    var (code, json) = Respond(method, path, body);
                    var payload = Encoding.UTF8.GetBytes(json);
                    var reason = code switch { 200 => "OK", 404 => "Not Found", 409 => "Conflict", 507 => "Insufficient Storage", _ => "Error" };
                    var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\n\r\n");
                    await s.WriteAsync(resp, _cts.Token);
                    await s.WriteAsync(payload, _cts.Token);
                    await s.FlushAsync(_cts.Token);
                }
            }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class PiCameraApiTests
{
    [Theory]
    [InlineData("http://172.30.1.81:8889/stream.mjpg", "http://172.30.1.81:8889")]
    [InlineData("http://pi.local:8889", "http://pi.local:8889")]
    [InlineData("https://h/x/y?z=1", "https://h:443")]
    [InlineData("garbage", "")]
    [InlineData("", "")]
    [InlineData("ftp://a/b", "")]
    public void BaseFromStreamUrl_StripsPath(string input, string expected)
    {
        Assert.Equal(expected, PiCameraApi.BaseFromStreamUrl(input));
    }

    [Fact]
    public async Task RecordStartStop_RoundTrip_AndConflictIsCameraException()
    {
        using var srv = new FakePiApiServer();
        using var api = new PiCameraApi(() => srv.StreamUrl);
        Assert.Equal(srv.Base, api.BaseUrl);

        var st = await api.RecordStartAsync();
        Assert.True(st.Recording);
        Assert.Equal("rec_x.avi", st.File);
        Assert.Equal("mjpeg-avi", st.Kind);
        Assert.Equal(24000, st.Disk!.FreeMb);

        var ex = await Assert.ThrowsAsync<CameraException>(() => api.RecordStartAsync());
        Assert.Contains("이미 녹화 중", ex.Message);
        Assert.Contains("409", ex.Message);

        var res = await api.RecordStopAsync();
        Assert.Equal("rec_x.avi", res.Name);
        Assert.Equal(2097152, res.Bytes);
        Assert.Equal(52, res.Frames);
        Assert.Equal(5.2, res.Seconds, 3);

        var reqs = srv.Snapshot();
        Assert.Equal(new[] { "POST /record/start", "POST /record/start", "POST /record/stop" }, reqs.Select(r => r.method + " " + r.path).ToArray());
    }

    [Fact]
    public async Task Status_List_Snapshot_Settings_Detect()
    {
        using var srv = new FakePiApiServer();
        using var api = new PiCameraApi(() => srv.StreamUrl);

        var status = await api.StatusAsync();
        Assert.Equal("9.9.9", status.Version);
        Assert.False(status.Record!.Recording);
        Assert.True(status.Detect!.Available);
        Assert.Equal("사람 1", status.Detect.Summary);
        Assert.Equal("사람", status.Detect.Objects[0].LabelKo);
        Assert.Equal("720p60", status.Settings!.Settings.RecordMode);
        Assert.True(status.Settings.Settings.Hflip);
        Assert.Equal(0.25, status.Settings.Settings.Brightness);
        Assert.Equal(new[] { 2592, 1944 }, status.Settings.StillSize);

        var list = await api.ListRecordingsAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal("image", list[1].Kind);

        var snap = await api.SnapshotSaveAsync();
        Assert.Equal((2592, 1944), (snap.Width, snap.Height));

        var info = await api.GetSettingsAsync();
        Assert.Equal("h264", info.RecordCapable);
        Assert.Contains("lores", info.BackendDetail);

        var applied = await api.SetSettingsAsync(new Dictionary<string, object> { ["record_mode"] = "480p90", ["brightness"] = 0.5 });
        Assert.True(applied.Restarted);
        Assert.Equal("480p90", applied.Settings.RecordMode);
        Assert.Equal(0.5, applied.Settings.Brightness);
        var sent = srv.Snapshot().Last(r => r.path == "/camera/settings");
        using var doc = JsonDocument.Parse(sent.body);
        Assert.Equal("480p90", doc.RootElement.GetProperty("record_mode").GetString());   // snake_case 그대로 보냄
        Assert.Equal(0.5, doc.RootElement.GetProperty("brightness").GetDouble());

        Assert.Equal("1100,1500,1900,-40,40", status.Servo!.Calib);
        var cal = await api.SetServoCalibAsync("600,1500,2400,-80,80");
        Assert.Equal("600,1500,2400,-80,80", cal.Calib);
        Assert.Equal("600,1500,2400,-80,80", srv.Calib);

        var det = await api.DetectSetAsync(true);
        Assert.True(det.Enabled);
        det = await api.DetectSetAsync(false);
        Assert.False(det.Enabled);
        var ds = await api.DetectStatusAsync();
        Assert.False(ds.Available);
        Assert.Contains("OpenCV", ds.Summary);
    }

    [Fact]
    public async Task ServerError_UsesErrorField()
    {
        using var srv = new FakePiApiServer();
        srv.Routes["/snapshot/save"] = (507, """{"error":"디스크 여유 공간이 100 MB 뿐입니다"}""");
        using var api = new PiCameraApi(() => srv.StreamUrl);
        var ex = await Assert.ThrowsAsync<CameraException>(() => api.SnapshotSaveAsync());
        Assert.StartsWith("디스크 여유 공간", ex.Message);
        Assert.Contains("507", ex.Message);
    }

    [Fact]
    public async Task ClosedPort_AndBadUrl_AreCameraExceptions()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var api = new PiCameraApi(() => $"http://127.0.0.1:{port}/stream.mjpg");
        var ex = await Assert.ThrowsAsync<CameraException>(() => api.RecordStatusAsync());
        Assert.Contains("연결할 수 없습니다", ex.Message);

        using var bad = new PiCameraApi(() => "not a url");
        var ex2 = await Assert.ThrowsAsync<CameraException>(() => bad.RecordStatusAsync());
        Assert.Contains("올바르지 않습니다", ex2.Message);
    }
}
