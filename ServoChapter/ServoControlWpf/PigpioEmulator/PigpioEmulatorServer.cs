using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

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
///   그 밖의 명령 → 0
///
/// 서보의 물리 움직임도 흉내 낸다: 목표 펄스를 향해 0.19 s/60° (HS-311 @4.8 V) 속도로 움직이는 "현재 각도".
/// </summary>
public sealed class PigpioEmulatorServer : IDisposable
{
    public const uint CmdServo = 8;
    public const uint CmdHwVer = 17;
    public const uint CmdPigpv = 26;

    public const int HardwareRevision = 0xc03111;   // Pi 4B
    public const int PigpioVersion = 79;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, int> _pulses = new();   // gpio → 현재 펄스폭 (0 = 꺼짐)
    private readonly Dictionary<int, double> _angles = new(); // gpio → 흉내 낸 서보 각도 (°, 1500 µs = 0, 10 µs/°)
    private readonly Dictionary<int, DateTime> _angleStamp = new();
    private int _clients;
    private long _commandCount;
    private Task? _acceptLoop;

    public PigpioEmulatorServer(IPAddress bind, int port)
    {
        _listener = new TcpListener(bind, port);
    }

    public PigpioEmulatorServer(string bindAddress = "127.0.0.1", int port = 8888)
        : this(IPAddress.Parse(bindAddress), port)
    {
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
        var target = (pulse - 1500) / 10.0;                   // 10 µs/° (HS-311)
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
                    var (res, desc) = Handle(cmd, p1, p2);
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

    private (int res, string desc) Handle(uint cmd, uint p1, uint p2)
    {
        switch (cmd)
        {
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
                if (changed) PulseChanged?.Invoke(gpio, pulse);
                return (0, pulse == 0 ? $"SERVO gpio={gpio} 출력 끔" : $"SERVO gpio={gpio} {pulse} µs");
            }
            case CmdHwVer:
                return (HardwareRevision, $"HWVER → 0x{HardwareRevision:x}");
            case CmdPigpv:
                return (PigpioVersion, $"PIGPV → {PigpioVersion}");
            default:
                return (0, $"cmd {cmd} (p1={p1}, p2={p2}) → 0 (흉내 내지 않는 명령)");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _cts.Dispose();
    }
}
