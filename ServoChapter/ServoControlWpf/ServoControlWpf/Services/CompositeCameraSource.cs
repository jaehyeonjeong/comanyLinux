namespace ServoControlWpf.Services;

/// <summary>
/// 여러 카메라 소스를 하나로 묶어 CameraViewModel 에 넘긴다 (ViewModel 은 소스가 하나뿐이라고 생각한다).
///   - 목록: local(웹캠) 목록 + network(Pi MJPEG) 목록. local 이 실패(권한 없음 등)해도 network 항목은 넣는다.
///   - 시작: 장치 ID 가 "mjpeg:" 로 시작하면 network, 아니면 local. 다른 쪽은 먼저 멈춘다.
///   - FrameArrived: 활성 소스의 프레임만 흘려보낸다.
/// </summary>
public sealed class CompositeCameraSource : ICameraSource
{
    private readonly ICameraSource _local;
    private readonly ICameraSource _network;
    private ICameraSource? _active;

    public CompositeCameraSource(ICameraSource local, ICameraSource network)
    {
        _local = local;
        _network = network;
        _local.FrameArrived += f => { if (ReferenceEquals(_active, _local)) FrameArrived?.Invoke(f); };
        _network.FrameArrived += f => { if (ReferenceEquals(_active, _network)) FrameArrived?.Invoke(f); };
    }

    public static bool IsNetworkId(string? id) => id is not null && id.StartsWith("mjpeg:", StringComparison.Ordinal);

    public bool IsRunning => _active?.IsRunning == true;
    public event Action<CameraFrame>? FrameArrived;

    public async Task<IReadOnlyList<CameraDevice>> ListAsync()
    {
        var result = new List<CameraDevice>();
        string? error = null;
        try { result.AddRange(await _local.ListAsync().ConfigureAwait(false)); }
        catch (CameraException ex) { error = ex.Message; }
        try { result.AddRange(await _network.ListAsync().ConfigureAwait(false)); }
        catch (CameraException ex) { error ??= ex.Message; }
        if (result.Count == 0 && error is not null)
            throw new CameraException(error);
        return result;
    }

    public async Task StartAsync(string deviceId, CancellationToken ct = default)
    {
        var target = IsNetworkId(deviceId) ? _network : _local;
        var other = ReferenceEquals(target, _network) ? _local : _network;
        if (other.IsRunning)
            await other.StopAsync().ConfigureAwait(false);
        _active = target;
        try
        {
            await target.StartAsync(deviceId, ct).ConfigureAwait(false);
        }
        catch
        {
            _active = null;
            throw;
        }
    }

    public async Task StopAsync()
    {
        var a = _active;
        _active = null;
        if (a is not null)
            await a.StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _active = null;
        _local.Dispose();
        _network.Dispose();
    }
}
