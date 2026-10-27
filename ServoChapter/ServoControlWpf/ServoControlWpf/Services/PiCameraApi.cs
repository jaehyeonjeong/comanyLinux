using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServoControlWpf.Services;

/// <summary>
/// pi_display 의 HTTP API (포트 8889) 클라이언트 — 녹화 시작/정지, 5 MP 사진, 카메라 설정, 인식 켜기/끄기, 상태 읽기.
///
///   주소는 호출 때마다 baseUrlProvider 로 받는다 (스트림 URL http://host:8889/stream.mjpg 에서 경로를 떼어 http://host:8889).
///   응답은 모두 JSON. 오류(4xx/5xx)는 서버가 {"error": "…"} 로 주므로 그 글을 CameraException 으로 올린다.
///   연결 실패·시간 초과도 CameraException — 화면은 메시지 한 줄만 보여 주면 된다.
/// </summary>
public sealed class PiCameraApi : IDisposable
{
    private readonly Func<string> _baseUrlProvider;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public PiCameraApi(Func<string> baseUrlProvider)
    {
        _baseUrlProvider = baseUrlProvider;
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = TimeSpan.FromSeconds(15),     // 5 MP 사진은 1~2 초 걸릴 수 있음
        };
    }

    /// <summary>지금 쓰이는 API 기본 주소 (예 http://172.30.1.81:8889)</summary>
    public string BaseUrl => BaseFromStreamUrl(_baseUrlProvider());

    /// <summary>스트림 URL(…/stream.mjpg) 이든 기본 주소든 → "http://host:port" 로 정리. 잘못된 값이면 "".</summary>
    public static string BaseFromStreamUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            return "";
        return $"{u.Scheme}://{u.Host}:{u.Port}";
    }

    // ───────── 녹화 · 사진 ─────────
    public Task<PiRecordStatus> RecordStartAsync(CancellationToken ct = default) => PostAsync<PiRecordStatus>("/record/start", null, ct);
    public Task<PiRecordResult> RecordStopAsync(CancellationToken ct = default) => PostAsync<PiRecordResult>("/record/stop", null, ct);
    public Task<PiRecordStatus> RecordStatusAsync(CancellationToken ct = default) => GetAsync<PiRecordStatus>("/record/status", ct);
    public Task<List<PiRecordingInfo>> ListRecordingsAsync(CancellationToken ct = default) => GetAsync<List<PiRecordingInfo>>("/recordings.json", ct);
    public Task<PiSnapshotResult> SnapshotSaveAsync(CancellationToken ct = default) => PostAsync<PiSnapshotResult>("/snapshot/save", null, ct);

    // ───────── 카메라 설정 ─────────
    public Task<PiCameraSettingsInfo> GetSettingsAsync(CancellationToken ct = default) => GetAsync<PiCameraSettingsInfo>("/camera/settings", ct);
    /// <summary>바꿀 키만 넘긴다 (record_mode · hflip · vflip · brightness · contrast · saturation · awb)</summary>
    public Task<PiSettingsApplyResult> SetSettingsAsync(IReadOnlyDictionary<string, object> partial, CancellationToken ct = default)
        => PostAsync<PiSettingsApplyResult>("/camera/settings", partial, ct);

    // ───────── 인식 ─────────
    public Task<PiDetectStatus> DetectSetAsync(bool on, CancellationToken ct = default) => PostAsync<PiDetectStatus>(on ? "/detect/on" : "/detect/off", null, ct);
    public Task<PiDetectStatus> DetectStatusAsync(CancellationToken ct = default) => GetAsync<PiDetectStatus>("/detect/status", ct);

    // ───────── 서보 보정값 (Pi 화면의 각도 계산을 WPF 와 맞춤) ─────────
    public Task<PiCalibResult> SetServoCalibAsync(string calib, CancellationToken ct = default)
        => PostAsync<PiCalibResult>("/servo/calib", new Dictionary<string, object> { ["calib"] = calib }, ct);

    // ───────── 전체 상태 ─────────
    public Task<PiStatus> StatusAsync(CancellationToken ct = default) => GetAsync<PiStatus>("/status.json", ct);

    // ═════════ 내부 ═════════
    private Uri MakeUri(string path)
    {
        var b = BaseUrl;
        if (b.Length == 0)
            throw new CameraException($"Pi 카메라 URL 이 올바르지 않습니다: '{_baseUrlProvider()}'");
        return new Uri(b + path);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var uri = MakeUri(path);
        try
        {
            using var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            return await ReadAsync<T>(resp, uri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw Unreachable(uri, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw Unreachable(uri, ex); }
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
    {
        var uri = MakeUri(path);
        try
        {
            // JsonContent 는 길이를 미리 모르면 chunked 로 보내는데, Pi 의 BaseHTTPRequestHandler 는 Content-Length 만 읽는다 → 문자열로 만들어 보냄
            using var content = new StringContent(body is null ? "" : JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(uri, content, ct).ConfigureAwait(false);
            return await ReadAsync<T>(resp, uri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw Unreachable(uri, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw Unreachable(uri, ex); }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, Uri uri, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = TryError(text) ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            throw new CameraException($"{msg} (HTTP {(int)resp.StatusCode})");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new CameraException($"Pi 응답이 비어 있습니다: {uri.AbsolutePath}");
        }
        catch (JsonException ex)
        {
            throw new CameraException($"Pi 응답을 읽을 수 없습니다 ({uri.AbsolutePath}): {ex.Message}", ex);
        }
    }

    private static string? TryError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static CameraException Unreachable(Uri uri, Exception ex)
        => new($"Pi 카메라 서버({uri.Scheme}://{uri.Authority})에 연결할 수 없습니다.\n({ex.Message})\npi_display 가 실행 중인지 확인하세요.", ex);

    public void Dispose() => _http.Dispose();
}

// ───────── 응답 모양 (pi_display/pidisplay/mjpeg_server.py 와 1:1, snake_case) ─────────

public sealed class PiDisk
{
    public long FreeMb { get; set; }
    public long UsedMb { get; set; }
    public double MaxGb { get; set; }
}

public sealed class PiRecordResult
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public double Seconds { get; set; }
    public int Frames { get; set; }
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class PiRecordStatus
{
    public bool Recording { get; set; }
    public string Kind { get; set; } = "";
    public string? File { get; set; }
    public double Seconds { get; set; }
    public long Bytes { get; set; }
    public int Frames { get; set; }
    public string Dir { get; set; } = "";
    public double MaxMinutes { get; set; }
    public PiDisk? Disk { get; set; }
    public PiRecordResult? Last { get; set; }
}

public sealed class PiRecordingInfo
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public long Mtime { get; set; }
    public string Kind { get; set; } = "";
}

public sealed class PiSnapshotResult
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    public string Note { get; set; } = "";
}

public sealed class PiCameraSettings
{
    public string RecordMode { get; set; } = "1080p30";
    public bool Hflip { get; set; }
    public bool Vflip { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public string Awb { get; set; } = "auto";
}

public sealed class PiCameraSettingsInfo
{
    public PiCameraSettings Settings { get; set; } = new();
    public string[] RecordModes { get; set; } = Array.Empty<string>();
    public string[] AwbModes { get; set; } = Array.Empty<string>();
    public int[] RecordSize { get; set; } = Array.Empty<int>();
    public int RecordFps { get; set; }
    public int[] StillSize { get; set; } = Array.Empty<int>();
    public string RecordCapable { get; set; } = "";
    public bool ControlsSupported { get; set; }
    public string BackendDetail { get; set; } = "";
    public int Restarts { get; set; }
}

public sealed class PiSettingsApplyResult
{
    public PiCameraSettings Settings { get; set; } = new();
    public bool Restarted { get; set; }
    public bool Applied { get; set; }
}

public sealed class PiDetection
{
    public string Label { get; set; } = "";
    public string LabelKo { get; set; } = "";
    public double Conf { get; set; }
    public double[] Box { get; set; } = Array.Empty<double>();
}

public sealed class PiDetectStatus
{
    public bool Available { get; set; }
    public bool Enabled { get; set; }
    public string Model { get; set; } = "";
    public string Error { get; set; } = "";
    public int Every { get; set; }
    public double Conf { get; set; }
    public double Fps { get; set; }
    public int InferMs { get; set; }
    public List<PiDetection> Objects { get; set; } = new();
    public string Summary { get; set; } = "";
}

public sealed class PiServoStatus
{
    public int Gpio { get; set; }
    public int? Pulse { get; set; }
    public double? Angle { get; set; }
    public bool OutputOn { get; set; }
    public bool Connected { get; set; }
    /// <summary>Pi 화면 프로그램이 각도 계산에 쓰는 보정값 "min,neutral,max,minAngle,maxAngle"</summary>
    public string Calib { get; set; } = "";
}

public sealed class PiCalibResult
{
    public string Calib { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class PiStatus
{
    public string Version { get; set; } = "";
    public PiServoStatus? Servo { get; set; }
    public PiRecordStatus? Record { get; set; }
    public PiDetectStatus? Detect { get; set; }
    public PiCameraSettingsInfo? Settings { get; set; }
}
