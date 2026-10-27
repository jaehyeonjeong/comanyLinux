namespace ServoControlWpf.Services;

/// <summary>카메라 장치 하나 (Id 는 운영체제가 주는 고유 문자열, Name 은 표시용)</summary>
public sealed record CameraDevice(string Id, string Name)
{
    public override string ToString() => Name;   // 콤보박스·화면 읽기 프로그램에는 이름만 (레코드 기본 문자열은 Id 까지 나옴)
}

/// <summary>영상 한 장. 픽셀은 BGRA 8비트(한 픽셀 4바이트), 한 줄 길이 = Width × 4</summary>
public readonly record struct CameraFrame(int Width, int Height, byte[] Bgra);

/// <summary>카메라를 열 수 없을 때 (권한 없음, 다른 프로그램이 사용 중, 장치 없음 등)</summary>
public sealed class CameraException : Exception
{
    public CameraException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// "영상이 나오는 장치" 의 공통 규격. 지금은 노트북 웹캠(WinRtCameraSource)만 있고,
/// 나중에 Pi 카메라(네트워크 스트림)를 같은 규격으로 붙이면 화면 쪽은 바꿀 것이 없다.
/// FrameArrived 는 백그라운드 스레드에서 호출된다.
/// </summary>
public interface ICameraSource : IDisposable
{
    Task<IReadOnlyList<CameraDevice>> ListAsync();
    Task StartAsync(string deviceId, CancellationToken ct = default);   // 실패하면 CameraException
    Task StopAsync();
    bool IsRunning { get; }
    event Action<CameraFrame>? FrameArrived;
}

/// <summary>카메라 기능이 없는 환경(테스트 등)용 빈 구현</summary>
public sealed class NullCameraSource : ICameraSource
{
    public Task<IReadOnlyList<CameraDevice>> ListAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>(Array.Empty<CameraDevice>());
    public Task StartAsync(string deviceId, CancellationToken ct = default) => throw new CameraException("이 환경에는 카메라 기능이 없습니다.");
    public Task StopAsync() => Task.CompletedTask;
    public bool IsRunning => false;
    public event Action<CameraFrame>? FrameArrived { add { } remove { } }
    public void Dispose() { }
}
