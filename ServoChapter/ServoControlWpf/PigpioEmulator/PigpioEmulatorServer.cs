using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PigpioEmulator;

/// <summary>
/// 가짜 pigpiod (Raspberry Pi 없이 테스트하기 위한 에뮬레이터)
///
/// 실제 pigpiod 와 같은 16바이트 소켓 프로토콜로 응답하므로, ServoControl(Qt) 과 ServoControlWpf 는
/// 자기가 진짜 Pi 에 붙었는지 에뮬레이터에 붙었는지 구별하지 못한다. 즉 TCP·바이트 열·오류 처리 경로를 그대로 검증한다.
///
///   요청 16바이트 = cmd, p1, p2, p3  (uint32 LE)
///   응답 16바이트 = cmd, p1, p2, res (int32 LE, 0 이상 성공 / 음수 오류)
///
/// 흉내 내는 명령
///   SERVO(8)  gpio, 펄스폭 → 0 또는 500~2500 이면 저장하고 0, 아니면 −7(PI_BAD_PULSEWIDTH); gpio > 31 이면 −2(PI_BAD_USER_GPIO)
///   HWVER(17) → 0xc03111 (Pi 4 Model B 리비전), PIGPV(26) → 79 (pigpio 버전)
///   GPW(84)   gpio → 지금 펄스폭 (0 = 꺼짐), 서보로 쓴 적 없으면 −93(PI_NOT_SERVO_GPIO)
///   서버는 접속이 끊겨도 펄스·각도를 기억하므로(실제 pigpiod 와 같음), 다시 접속한 프로그램이 GPW 로 이전 상태를 알 수 있다.
///   CF1(87)   보정값 전달 (ServoCalibration 참고) — WPF 의 보정값과 같은 공식으로 각도를 계산하게 한다
///   그 밖의 명령 → 0
///
/// 서보의 물리 움직임도 흉내 낸다: 목표 펄스를 향해 0.19 s/60° (HS-311 @4.8 V) 속도로 움직이는 "현재 각도".
/// 각도는 <see cref="Calibration"/>(기본 HS-311 공식값, WPF 가 SetCalibration/CF1 로 바꿈)으로 계산 — 펄스가 진실이고 각도는 표시용.
///
/// 상태 파일(StateFile): 지정하면 펄스·흉내 낸 각도를 JSON 으로 저장하고 다음에 띄울 때 되살린다.
///   → 프로그램을 껐다 켜도 "같은 서보" 가 같은 자리에 있는 것처럼 동작 (실제 서보는 전원이 끊겨도 물리적으로 그 자리에 머묾).
/// </summary>
public sealed class PigpioEmulatorServer : IDisposable
{
    public const uint CmdServo = 8;
    public const uint CmdHwVer = 17;
    public const uint CmdPigpv = 26;
    public const uint CmdGetServoPulsewidth = 84;   // GPW

    public const int HardwareRevision = 0xc03111;   // Pi 4B
    public const int PigpioVersion = 79;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, int> _pulses = new();   // gpio → 현재 펄스폭 (0 = 꺼짐)
    private readonly Dictionary<int, double> _angles = new(); // gpio → 흉내 낸 서보 각도 (°, Calibration 기준)
    private readonly Dictionary<int, DateTime> _angleStamp = new();
    private int _clients;
    private long _commandCount;
    private Task? _acceptLoop;
    private ServoCalibration _calib = ServoCalibration.Default;

    /// <summary>펄스 → 각도 보정값 (기본 HS-311 공식값). WPF 가 자기 보정값으로 맞춰 준다.</summary>
    public ServoCalibration Calibration { get { lock (_lock) return _calib; } }

    /// <summary>보정값 교체. 기억 중인 각도는 새 보정값으로 다시 계산한다(펄스는 그대로).</summary>
    public void SetCalibration(ServoCalibration calib)
    {
        if (!calib.IsValid()) throw new ArgumentException("보정값이 올바르지 않습니다: " + calib, nameof(calib));
        lock (_lock)
        {
            _calib = calib;
            foreach (var (gpio, pulse) in _pulses)
                if (pulse != 0) _angles[gpio] = calib.PulseToAngle(pulse);
        }
        SaveState();
    }

    public PigpioEmulatorServer(IPAddress bind, int port, string? stateFile = null)
    {
        _listener = new TcpListener(bind, port);
        StateFile = stateFile;
    }

    public PigpioEmulatorServer(string bindAddress = "127.0.0.1", int port = 8888, string? stateFile = null)
        : this(IPAddress.Parse(bindAddress), port, stateFile)
    {
    }

    /// <summary>상태를 저장·복원할 JSON 파일. null 이면 메모리에만 둔다 (테스트용).</summary>
    public string? StateFile { get; }

    /// <summary>기본 상태 파일 위치: Windows %APPDATA%\VI\PigpioEmulator.json, 리눅스 ~/.config/VI/PigpioEmulator.json</summary>
    public static string DefaultStateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VI", "PigpioEmulator.json");

    private sealed class SavedGpio
    {
        public int Pulse { get; set; }
        public double Angle { get; set; }
    }

    /// <summary>상태 파일 모양 (v1.3.1 부터 보정값도 함께). 옛 파일(gpio → {Pulse,Angle} 사전)도 읽는다.</summary>
    private sealed class SavedState
    {
        public string? Calib { get; set; }
        public Dictionary<string, SavedGpio> Gpios { get; set; } = new();
    }

    /// <summary>상태 파일을 읽어 펄스·각도·보정값을 되살린다. 펄스가 켜져 있던 핀은 그동안 목표에 도달했다고 본다.</summary>
    private void LoadState()
    {
        if (StateFile is null || !File.Exists(StateFile)) return;
        try
        {
            var text = File.ReadAllText(StateFile);
            Dictionary<string, SavedGpio>? saved;
            string? calibText = null;
            using (var doc = JsonDocument.Parse(text))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Gpios", out _))
                {
                    var st = JsonSerializer.Deserialize<SavedState>(text);
                    saved = st?.Gpios;
                    calibText = st?.Calib;
                }
                else
                {
                    saved = JsonSerializer.Deserialize<Dictionary<string, SavedGpio>>(text);
                }
            }
            if (saved is null) return;
            lock (_lock)
            {
                if (ServoCalibration.TryParse(calibText, out var c)) _calib = c;
                foreach (var (key, s) in saved)
                {
                    if (!int.TryParse(key, out var gpio) || gpio < 0 || gpio > 31) continue;
                    if (s.Pulse != 0 && (s.Pulse < 500 || s.Pulse > 2500)) continue;
                    _pulses[gpio] = s.Pulse;
                    _angles[gpio] = s.Pulse != 0 ? _calib.PulseToAngle(s.Pulse) : Math.Clamp(s.Angle, -90, 90);
                    _angleStamp[gpio] = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 손상된 상태 파일은 무시하고 빈 상태로 시작
        }
    }

    /// <summary>현재 펄스·각도·보정값을 상태 파일에 쓴다 (SERVO 명령마다, 보정값이 바뀔 때, 그리고 종료 때).</summary>
    private void SaveState()
    {
        if (StateFile is null) return;
        SavedState snap;
        lock (_lock)
        {
            snap = new SavedState { Calib = _calib.ToCalibString() };
            foreach (var (gpio, pulse) in _pulses)
            {
                AdvanceAngle(gpio);
                snap.Gpios[gpio.ToString()] = new SavedGpio { Pulse = pulse, Angle = _angles.TryGetValue(gpio, out var a) ? a : 0 };
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 치명적이지 않음
        }
    }

    public sealed record CommandEvent(uint Cmd, uint P1, uint P2, int Result, string Description);

    /// <summary>명령 하나를 처리할 때마다 (스레드 풀에서) 호출</summary>
    public event Action<CommandEvent>? CommandReceived;

    /// <summary>SERVO 명령으로 펄스폭이 바뀔 때 (gpio, 펄스폭 µs)</summary>
    public event Action<int, int>? PulseChanged;

    /// <summary>클라이언트 접속·해제 (현재 접속 수)</summary>
    public event Action<int>? ClientCountChanged;

    public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;
    public int ClientCount => _clients;
    public long CommandCount => Interlocked.Read(ref _commandCount);
    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    /// <summary>듣기 시작. 포트가 이미 쓰이고 있으면 SocketException.</summary>
    public void Start()
    {
        LoadState();
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int GetPulse(int gpio)
    {
        lock (_lock) return _pulses.TryGetValue(gpio, out var p) ? p : 0;
    }

    /// <summary>흉내 낸 서보의 현재 각도 (HS-311 속도 0.19 s/60° 로 목표를 향해 움직이는 값)</summary>
    public double GetSimulatedAngle(int gpio)
    {
        lock (_lock)
        {
            AdvanceAngle(gpio);
            return _angles.TryGetValue(gpio, out var a) ? a : 0;
        }
    }

    public IReadOnlyDictionary<int, int> SnapshotPulses()
    {
        lock (_lock) return new Dictionary<int, int>(_pulses);
    }

    private void AdvanceAngle(int gpio)
    {
        var now = DateTime.UtcNow;
        var pulse = _pulses.TryGetValue(gpio, out var p) ? p : 0;
        var cur = _angles.TryGetValue(gpio, out var a) ? a : 0;
        var last = _angleStamp.TryGetValue(gpio, out var t) ? t : now;
        _angleStamp[gpio] = now;
        if (pulse == 0) return;   // 출력 꺼짐: 그 자리에 멈춤
        var target = _calib.PulseToAngle(pulse);              // WPF 와 같은 보정값으로 (기본 HS-311 10 µs/°)
        var maxStep = 60.0 / 0.19 * (now - last).TotalSeconds; // 0.19 s/60°
        var diff = target - cur;
        _angles[gpio] = Math.Abs(diff) <= maxStep ? target : cur + Math.Sign(diff) * maxStep;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeClientAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        ClientCountChanged?.Invoke(Interlocked.Increment(ref _clients));
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var buf = new byte[16];
                var resp = new byte[16];
                while (!_cts.IsCancellationRequested)
                {
                    var got = 0;
                    while (got < 16)
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(got, 16 - got), _cts.Token).ConfigureAwait(false);
                        if (n == 0) return;   // 상대가 끊음
                        got += n;
                    }

                    var cmd = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
                    var p1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
                    var p2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4));
                    var p3 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(12, 4));
                    var (res, desc) = Handle(cmd, p1, p2, p3);
                    Interlocked.Increment(ref _commandCount);

                    buf.AsSpan(0, 12).CopyTo(resp);
                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(12, 4), res);
                    await stream.WriteAsync(resp, _cts.Token).ConfigureAwait(false);

                    CommandReceived?.Invoke(new CommandEvent(cmd, p1, p2, res, desc));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            ClientCountChanged?.Invoke(Interlocked.Decrement(ref _clients));
        }
    }

    private (int res, string desc) Handle(uint cmd, uint p1, uint p2, uint p3 = 0)
    {
        switch (cmd)
        {
            case ServoCalibration.CmdCustom1:
            {
                // WPF 가 보정값을 알려 줌 (p1=1 펄스 세 값, p1=2 각도 두 값). 잘못된 값은 −7 로 거부하고 이전 값 유지
                ServoCalibration? next;
                lock (_lock)
                    next = p1 == ServoCalibration.SubPulses ? _calib.WithPackedPulses(p2, p3)
                         : p1 == ServoCalibration.SubAngles ? _calib.WithPackedAngles(p2, p3) : null;
                if (next is null)
                    return (-7, $"CF1 p1={p1} (p2={p2}, p3={p3}) → 보정값으로 쓸 수 없음");
                SetCalibration(next);
                return (0, p1 == ServoCalibration.SubPulses
                    ? $"CALIB 펄스 {next.MinPulse}/{next.NeutralPulse}/{next.MaxPulse} µs"
                    : $"CALIB 각도 {next.MinAngle:0.#}°~{next.MaxAngle:0.#}° → 보정값 {next}");
            }
            case CmdServo:
            {
                if (p1 > 31)
                    return (-2, $"SERVO gpio={p1} → PI_BAD_USER_GPIO");
                if (p2 != 0 && (p2 < 500 || p2 > 2500))
                    return (-7, $"SERVO gpio={p1} pulse={p2} → PI_BAD_PULSEWIDTH");
                var gpio = (int)p1;
                var pulse = (int)p2;
                bool changed;
                lock (_lock)
                {
                    AdvanceAngle(gpio);
                    changed = !_pulses.TryGetValue(gpio, out var old) || old != pulse;
                    _pulses[gpio] = pulse;
                }
                if (changed)
                {
                    SaveState();
                    PulseChanged?.Invoke(gpio, pulse);
                }
                return (0, pulse == 0 ? $"SERVO gpio={gpio} 출력 끔" : $"SERVO gpio={gpio} {pulse} µs");
            }
            case CmdGetServoPulsewidth:
            {
                // 실제 pigpiod 처럼: 서보 펄스를 설정한 적 없는 핀이면 −93(PI_NOT_SERVO_GPIO), 아니면 지금 펄스폭(0 = 꺼짐)
                if (p1 > 31)
                    return (-2, $"GPW gpio={p1} → PI_BAD_USER_GPIO");
                lock (_lock)
                {
                    if (!_pulses.TryGetValue((int)p1, out var pw))
                        return (-93, $"GPW gpio={p1} → PI_NOT_SERVO_GPIO (서보로 쓴 적 없음)");
                    return (pw, pw == 0 ? $"GPW gpio={p1} → 0 (출력 꺼짐)" : $"GPW gpio={p1} → {pw} µs (기억하고 있던 펄스)");
                }
            }
            case CmdHwVer:
                return (HardwareRevision, $"HWVER → 0x{HardwareRevision:x}");
            case CmdPigpv:
                return (PigpioVersion, $"PIGPV → {PigpioVersion}");
            default:
                return (0, $"cmd {cmd} (p1={p1}, p2={p2}) → 0 (흉내 내지 않는 명령)");
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;   // 두 번 불려도 안전 (using + 명시적 Dispose)
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _cts.Dispose();
        SaveState();   // 흉내 낸 각도까지 저장 → 다음 실행에서 같은 자리에서 시작
    }
}
