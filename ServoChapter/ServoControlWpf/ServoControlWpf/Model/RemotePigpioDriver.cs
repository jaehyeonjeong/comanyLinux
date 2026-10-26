namespace ServoControlWpf.Model;

/// <summary>
/// [Model] TCP 로 Raspberry Pi 의 pigpio 데몬(pigpiod)에 접속해 서보 펄스를 출력  (Qt: model/remotepigpiodriver.*)
///
/// 접속 주소만 다르게 주면 두 가지 길 모두 이 클래스 하나로 처리된다.
///   - 직접 연결  : host = 192.168.10.2, port = 8888  (이더넷으로 Pi 의 pigpiod 에 바로)
///   - WSL 터널  : host = 127.0.0.1, port = 18888    (wsl.exe 안의 ssh -L 이 Pi 의 8888 로 전달)
/// 터널 프로세스의 수명은 <see cref="WslSshTunnel"/> 이 가지고 있다가, 이 드라이버가 Dispose 될 때 함께 닫는다.
/// </summary>
public sealed class RemotePigpioDriver : IServoDriver
{
    private readonly PigpioClient _client;
    private readonly IDisposable? _tunnel;     // WSL 터널(없으면 null)
    private readonly string _label;
    private int _gpio = -1;                    // 아직 출력한 핀 없음

    public RemotePigpioDriver(string host, int port, IDisposable? tunnel = null, string? label = null)
    {
        _client = new PigpioClient(host, port);
        _tunnel = tunnel;
        _label = label ?? $"pigpio @ {host}:{port}";
    }

    public string Name => $"{_label} · GPIO{(_gpio < 0 ? "?" : _gpio)}";
    public string Info { get; private set; } = string.Empty;
    public bool IsRemote => true;
    public bool IsLinkAlive => _client.IsConnected;

    public void Open(int gpio)
    {
        if (!_client.IsConnected)
        {
            _client.Connect();
            var hw = _client.Command(PigpioClient.CmdHwVer, 0, 0);
            var ver = _client.Command(PigpioClient.CmdPigpv, 0, 0);
            Info = $"Pi 리비전 {hw:x} · pigpio v{ver}";
        }

        if (_gpio >= 0 && gpio != _gpio)
            Release();   // 이전 핀 출력 정지
        _gpio = gpio;
    }

    public void WritePulse(int microseconds)
        => _client.Command(PigpioClient.CmdServo, (uint)_gpio, (uint)microseconds);

    /// <summary>
    /// 출력을 끈다. 통신 오류로 끊긴 뒤라도 서보가 힘을 계속 주지 않도록 0.5초 재접속해서 끔
    /// (pigpiod 는 접속이 끊겨도 마지막 펄스를 계속 내보내기 때문).
    /// </summary>
    public void Release()
    {
        if (_gpio < 0)
            return;
        try
        {
            if (!_client.IsConnected)
                _client.Connect(500);
            _client.Command(PigpioClient.CmdServo, (uint)_gpio, 0);
        }
        catch (ServoException)
        {
            // 끄기 실패는 무시 (이미 끊긴 상태)
        }
    }

    public void Dispose()
    {
        Release();          // 프로그램이 끝나면 서보 출력을 끔
        _client.Dispose();
        _tunnel?.Dispose(); // WSL 터널이 있으면 ssh 프로세스 종료
    }
}
