namespace ServoControlWpf.Model;

/// <summary>[Model] 하드웨어 없이 동작하는 가짜 드라이버 (Pi에 연결하기 전 화면 테스트용)</summary>
public sealed class SimulatedDriver : IServoDriver
{
    private int _gpio = 18;

    public string Name => "연결 안 됨 (시뮬레이션)";
    public string Info => string.Empty;
    public bool IsRemote => false;
    public bool IsLinkAlive => true;
    public bool ReleaseOnDispose { get; set; } = true;

    public void Open(int gpio) => _gpio = gpio;
    public void WritePulse(int microseconds) { /* 화면만 움직임 */ }
    public void Release() { }
    public int? ReadPulse() => null;     // 실제 장치가 없으니 알 수 없음
    public void Dispose() { }
}
