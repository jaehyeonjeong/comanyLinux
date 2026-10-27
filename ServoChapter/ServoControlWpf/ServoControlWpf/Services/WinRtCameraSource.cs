using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace ServoControlWpf.Services;

/// <summary>
/// Windows 내장 카메라 API(WinRT MediaCapture + MediaFrameReader)로 웹캠 영상을 받는다. 외부 패키지 없음.
///
///   장치 목록 : DeviceInformation.FindAllAsync(VideoCapture)
///   영상      : MediaCapture 초기화 → 프레임 소스(미리보기/녹화 스트림) 선택 → 1280 폭 이하 포맷 선택
///               → MediaFrameReader(BGRA8) → FrameArrived 마다 SoftwareBitmap 을 바이트 배열로 복사해 이벤트로 전달
///   주의      : 데스크톱 앱이 카메라를 쓰려면 Windows 설정 > 개인 정보 > 카메라 > "데스크톱 앱이 카메라에 액세스하도록 허용" 이 켜져 있어야 한다.
/// </summary>
public sealed class WinRtCameraSource : ICameraSource
{
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _reader is not null;
    public event Action<CameraFrame>? FrameArrived;

    public async Task<IReadOnlyList<CameraDevice>> ListAsync()
    {
        try
        {
            var devs = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devs.Select(d => new CameraDevice(d.Id, d.Name)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CameraException("카메라 장치 목록을 읽을 수 없습니다: " + ex.Message, ex);
        }
    }

    public async Task StartAsync(string deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var capture = new MediaCapture();
            try
            {
                // 모든 await 에 ConfigureAwait(false): UI 스레드로 되돌아오지 않게 해서, UI 스레드가 이 작업을 동기로 기다려도 교착이 생기지 않음
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,     // CPU 메모리로 받아야 바이트로 복사할 수 있음
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                }).AsTask().ConfigureAwait(false);

                // 미리보기 스트림이 있으면 그것, 없으면 녹화 스트림
                var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                             ?? capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                             ?? throw new CameraException("이 카메라에서 영상 스트림을 찾지 못했습니다.");

                // 너무 큰 해상도는 피한다 (화면 미리보기 용도): 1280 폭 이하 중 가장 큰 것, 같은 폭이면 프레임률 높은 것
                var fmt = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 800)
                    .OrderByDescending(f => f.VideoFormat.Width)
                    .ThenByDescending(f => f.FrameRate.Numerator / Math.Max(1.0, f.FrameRate.Denominator))
                    .FirstOrDefault();
                if (fmt is not null)
                    await source.SetFormatAsync(fmt).AsTask().ConfigureAwait(false);

                var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(false);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;   // 밀리면 오래된 프레임은 버림
                reader.FrameArrived += OnFrameArrived;
                var status = await reader.StartAsync().AsTask().ConfigureAwait(false);
                if (status != MediaFrameReaderStartStatus.Success)
                {
                    reader.FrameArrived -= OnFrameArrived;
                    reader.Dispose();
                    throw new CameraException("카메라 영상을 시작할 수 없습니다: " + status);
                }

                _capture = capture;
                _reader = reader;
            }
            catch (CameraException)
            {
                capture.Dispose();
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                capture.Dispose();
                throw new CameraException(
                    "카메라 사용 권한이 없습니다.\nWindows 설정 > 개인 정보 및 보안 > 카메라 에서 '카메라 액세스' 와 '데스크톱 앱이 카메라에 액세스하도록 허용' 을 켜 주세요.", ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                capture.Dispose();
                throw new CameraException(
                    "카메라를 열 수 없습니다: " + ex.Message + "\n다른 프로그램(화상 회의 등)이 카메라를 쓰고 있지 않은지 확인하세요.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try { await reader.StopAsync().AsTask().ConfigureAwait(false); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            reader.Dispose();
        }
        capture?.Dispose();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var handler = FrameArrived;
        if (handler is null) return;
        using var frame = sender.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp is null) return;

        // SoftwareBitmap → 바이트 배열 (BGRA8). IBuffer 를 거쳐 복사한다 (WinRT 버퍼를 .NET 배열로 옮기는 가장 단순한 길)
        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        var size = (uint)(w * h * 4);
        var buffer = new Windows.Storage.Streams.Buffer(size);
        bmp.CopyToBuffer(buffer);
        var bytes = new byte[size];
        using (var dr = DataReader.FromBuffer(buffer))
            dr.ReadBytes(bytes);
        handler(new CameraFrame(w, h, bytes));
    }

    /// <summary>
    /// 프로그램 종료 때 UI 스레드에서 호출된다. 정지 작업을 스레드 풀에서 돌리고 최대 3초만 기다린다
    /// — UI 스레드에서 WinRT 비동기 완료를 동기로 기다리면 교착될 수 있고, 카메라 정리가 늦어도 종료는 막지 않기 위해.
    /// </summary>
    public void Dispose()
    {
        try { Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _gate.Dispose();
    }
}
