using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ServoControlWpf.Services;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 가짜 MJPEG HTTP 서버 (TcpListener — HttpListener 는 URL 예약 권한 문제가 있어 쓰지 않음).
/// 요청 한 줄을 읽고 200 + multipart 헤더를 보낸 뒤, 단색 JPEG 를 frameCount 장 보낸다.
/// </summary>
internal sealed class FakeMjpegServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _jpeg;
    private readonly int _frameCount;
    private readonly CancellationTokenSource _cts = new();
    public int Served;

    public FakeMjpegServer(int frameCount, Color color, int w = 16, int h = 12)
    {
        _frameCount = frameCount;
        _jpeg = MakeJpeg(w, h, color);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url => $"http://127.0.0.1:{Port}/stream.mjpg";

    public static byte[] MakeJpeg(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { px[i * 4] = c.B; px[i * 4 + 1] = c.G; px[i * 4 + 2] = c.R; px[i * 4 + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
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
                var buf = new byte[4096];
                var got = 0;
                while (got < buf.Length)
                {
                    var n = await s.ReadAsync(buf.AsMemory(got), _cts.Token);
                    if (n <= 0) return;
                    got += n;
                    if (Encoding.ASCII.GetString(buf, 0, got).Contains("\r\n\r\n")) break;
                }
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head, _cts.Token);
                for (var i = 0; i < _frameCount && !_cts.IsCancellationRequested; i++)
                {
                    var part = Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {_jpeg.Length}\r\n\r\n");
                    await s.WriteAsync(part, _cts.Token);
                    await s.WriteAsync(_jpeg, _cts.Token);
                    await s.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), _cts.Token);
                    await s.FlushAsync(_cts.Token);
                    Interlocked.Increment(ref Served);
                    await Task.Delay(30, _cts.Token);
                }
                // 다 보낸 뒤에도 클라이언트가 끊을 때까지 잠깐 유지
                try { await Task.Delay(2000, _cts.Token); } catch (OperationCanceledException) { }
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

public class MjpegCameraSourceTests
{
    [Fact]
    public async Task ReceivesFrames_DecodedToBgra()
    {
        using var server = new FakeMjpegServer(5, Colors.Red);
        using var src = new MjpegCameraSource(() => server.Url);
        var frames = new List<CameraFrame>();
        var got2 = new TaskCompletionSource();
        src.FrameArrived += f => { lock (frames) { frames.Add(f); if (frames.Count >= 2) got2.TrySetResult(); } };

        var devices = await src.ListAsync();
        Assert.Single(devices);
        Assert.Equal(MjpegCameraSource.DeviceId, devices[0].Id);

        await src.StartAsync(MjpegCameraSource.DeviceId);
        Assert.True(src.IsRunning);
        await Task.WhenAny(got2.Task, Task.Delay(5000));
        Assert.True(got2.Task.IsCompleted, "2 장 이상 받아야 함");

        CameraFrame f0;
        lock (frames) f0 = frames[0];
        Assert.Equal(16, f0.Width);
        Assert.Equal(12, f0.Height);
        Assert.Equal(16 * 12 * 4, f0.Bgra.Length);
        // 빨강 (JPEG 손실 허용)
        Assert.True(f0.Bgra[2] > 200, $"R={f0.Bgra[2]}");
        Assert.True(f0.Bgra[1] < 60, $"G={f0.Bgra[1]}");
        Assert.True(f0.Bgra[0] < 60, $"B={f0.Bgra[0]}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await src.StopAsync();
        Assert.False(src.IsRunning);
        Assert.True(sw.ElapsedMilliseconds < 1500, "정지는 1.5초 안에");
    }

    [Fact]
    public async Task ClosedPort_ThrowsCameraException_Quickly()
    {
        // 아무도 듣지 않는 포트
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        using var src = new MjpegCameraSource(() => $"http://127.0.0.1:{port}/stream.mjpg");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<CameraException>(() => src.StartAsync(MjpegCameraSource.DeviceId));
        Assert.Contains("연결할 수 없습니다", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 6000);
        Assert.False(src.IsRunning);
    }

    [Fact]
    public async Task BadUrl_ThrowsCameraException()
    {
        using var src = new MjpegCameraSource(() => "not a url");
        var ex = await Assert.ThrowsAsync<CameraException>(() => src.StartAsync(MjpegCameraSource.DeviceId));
        Assert.Contains("올바르지 않습니다", ex.Message);
    }

    [Fact]
    public void Decode_RejectsGarbage()
    {
        Assert.False(MjpegCameraSource.TryDecode(new byte[] { 1, 2, 3 }, out _));
        Assert.True(MjpegCameraSource.TryDecode(FakeMjpegServer.MakeJpeg(8, 8, Colors.Blue), out var f));
        Assert.Equal(8, f.Width);
    }
}
