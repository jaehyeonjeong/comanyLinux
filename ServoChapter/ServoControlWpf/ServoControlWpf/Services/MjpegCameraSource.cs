using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ServoControlWpf.Services;

/// <summary>
/// 네트워크 카메라: Pi 의 pi_display 가 내보내는 HTTP MJPEG 스트림(예 http://172.30.1.81:8889/stream.mjpg)을 받는다.
///
///   HttpClient(응답 헤더까지만 기다림) → 읽기 루프(스레드 풀) → MjpegStreamParser → JPEG → 같은 스레드에서 BGRA 로 디코드 → FrameArrived
///   끊기면 1→2→…→5 초 간격으로 다시 붙는다. StopAsync 는 취소 토큰으로 루프를 끝내고 최대 1 초 기다린다.
///   장치 목록은 네트워크를 건드리지 않고 "Pi 카메라 (네트워크)" 한 항목을 바로 돌려준다 (URL 은 호출 때마다 urlProvider 로 받음).
/// </summary>
public sealed class MjpegCameraSource : ICameraSource
{
    public const string DeviceId = "mjpeg:pi";
    public const string DeviceName = "Pi 카메라 (네트워크)";

    private readonly Func<string> _urlProvider;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MjpegCameraSource(Func<string> urlProvider)
    {
        _urlProvider = urlProvider;
        _http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,     // 스트림은 끝나지 않으므로 전체 타임아웃은 끔
        };
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public event Action<CameraFrame>? FrameArrived;

    /// <summary>마지막으로 연결을 시도한 URL (상태 표시용)</summary>
    public string? CurrentUrl { get; private set; }

    public Task<IReadOnlyList<CameraDevice>> ListAsync()
        => Task.FromResult<IReadOnlyList<CameraDevice>>(new[] { new CameraDevice(DeviceId, DeviceName) });

    public async Task StartAsync(string deviceId, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        var url = (_urlProvider() ?? "").Trim();
        CurrentUrl = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new CameraException($"Pi 카메라 URL 이 올바르지 않습니다: '{url}'\n예: http://172.30.1.81:8889/stream.mjpg");

        // 첫 연결은 여기서 직접 해 보고 실패하면 바로 CameraException (그 뒤의 끊김은 루프가 재접속)
        var cts = new CancellationTokenSource();
        HttpResponseMessage response;
        try
        {
            response = await OpenAsync(uri, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new CameraException($"Pi 카메라 서버({url})가 5초 안에 응답하지 않습니다.\npi_display 가 실행 중인지, 포트가 열려 있는지 확인하세요.");
        }
        catch (HttpRequestException ex)
        {
            throw new CameraException($"Pi 카메라 서버({url})에 연결할 수 없습니다.\n({ex.Message})\n\npi_display 가 실행 중인지, 포트 8889 가 열려 있는지 확인하세요.", ex);
        }
        _cts = cts;
        _loop = Task.Run(() => ReadLoopAsync(uri, response, cts.Token), CancellationToken.None);
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));          // 헤더까지 5 초
        var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        return resp;
    }

    private async Task ReadLoopAsync(Uri uri, HttpResponseMessage? first, CancellationToken ct)
    {
        var backoff = 1;
        var response = first;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                response ??= await OpenAsync(uri, ct).ConfigureAwait(false);
                backoff = 1;
                using (response)
                using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    await PumpAsync(stream, ct).ConfigureAwait(false);
                }
                response = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                response = null;
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(5, backoff + 1);
            }
        }
    }

    private async Task PumpAsync(Stream stream, CancellationToken ct)
    {
        var parser = new MjpegStreamParser();
        var chunk = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("스트림이 끊겼습니다.");
            parser.Feed(chunk.AsSpan(0, n));
            while (parser.TryTakeFrame(out var jpeg))
            {
                var handler = FrameArrived;
                if (handler is null) continue;
                if (TryDecode(jpeg, out var frame))
                    handler(frame);
            }
        }
    }

    /// <summary>JPEG → BGRA 바이트 (WPF 디코더, 배경 스레드에서 실행해도 됨)</summary>
    internal static bool TryDecode(byte[] jpeg, out CameraFrame frame)
    {
        frame = default;
        try
        {
            using var ms = new MemoryStream(jpeg);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var src = decoder.Frames[0];
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var w = conv.PixelWidth;
            var h = conv.PixelHeight;
            var bytes = new byte[w * h * 4];
            conv.CopyPixels(bytes, w * 4, 0);
            frame = new CameraFrame(w, h, bytes);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return false;          // 깨진 프레임은 버림
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null) return;
        cts.Cancel();
        if (loop is not null)
        {
            try { await Task.WhenAny(loop, Task.Delay(1000)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        cts.Dispose();
    }

    public void Dispose()
    {
        try { Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _http.Dispose();
    }
}
