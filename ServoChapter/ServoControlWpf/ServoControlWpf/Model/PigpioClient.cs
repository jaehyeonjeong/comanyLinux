using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;

namespace ServoControlWpf.Model;

/// <summary>
/// pigpiod 소켓 프로토콜 (기본 포트 8888) 을 말하는 가장 낮은 계층.
///
///   요청 16바이트 = cmd, p1, p2, p3  (각각 uint32, little-endian)
///   응답 16바이트 = cmd, p1, p2, res (res: int32, 0 이상 성공 / 음수 오류 코드)
///
/// PC에는 pigpio 라이브러리를 설치할 필요가 없고, TcpClient 만 사용한다.
/// Qt 의 RemotePigpioDriver::command() 를 따로 떼어 낸 클래스라서 가짜 pigpiod(TcpListener)로 단위 테스트할 수 있다.
/// </summary>
public sealed class PigpioClient : IDisposable
{
    public const uint CmdServo = 8;    // 서보 펄스폭 (0 = 끔, 500~2500 µs)
    public const uint CmdHwVer = 17;   // 하드웨어 리비전
    public const uint CmdPigpv = 26;   // pigpio 버전
    public const uint CmdGetServoPulsewidth = 84;   // GPW: 그 GPIO 에 지금 내보내는 서보 펄스폭 (0 = 꺼짐, −93 = 서보로 쓴 적 없음)
    public const uint CmdCustom1 = 87;              // CF1: 가짜 Pi 에 보정값 전달 (PigpioEmulator.ServoCalibration) — 에뮬레이터 모드에서만 사용

    /// <summary>가짜 Pi 에 WPF 보정값을 알려 준다 (CF1 두 번). 둘 다 0 을 돌려주면 true.</summary>
    public bool SendCalibration(PigpioEmulator.ServoCalibration calib)
    {
        var (pp2, pp3) = calib.PackPulses();
        var (ap2, ap3) = calib.PackAngles();
        var r1 = Command(CmdCustom1, PigpioEmulator.ServoCalibration.SubPulses, pp2, pp3);
        var r2 = Command(CmdCustom1, PigpioEmulator.ServoCalibration.SubAngles, ap2, ap3);
        return r1 == 0 && r2 == 0;
    }

    public const int DefaultTimeoutMs = 1500;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public string Host { get; }
    public int Port { get; }
    public int TimeoutMs { get; }

    public PigpioClient(string host, int port, int timeoutMs = DefaultTimeoutMs)
    {
        Host = host;
        Port = port;
        TimeoutMs = timeoutMs;
    }

    public bool IsConnected => _client is { Connected: true } && _stream is not null;

    /// <summary>timeoutMs 안에 접속한다. 실패하면 ServoException(LinkLost).</summary>
    public void Connect(int? timeoutMs = null)
    {
        Abort();
        var client = new TcpClient();
        try
        {
            var task = client.ConnectAsync(Host, Port);
            if (!task.Wait(timeoutMs ?? TimeoutMs))
                throw new SocketException((int)SocketError.TimedOut);
            if (task.IsFaulted)
                throw task.Exception!.GetBaseException();

            client.NoDelay = true;                 // 명령마다 바로 전송 (Qt LowDelayOption)
            client.SendTimeout = TimeoutMs;
            client.ReceiveTimeout = TimeoutMs;
            _client = client;
            _stream = client.GetStream();
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException or ObjectDisposedException)
        {
            client.Dispose();
            throw new ServoException(
                $"{Host}:{Port} 에 연결할 수 없습니다.\n({ex.GetBaseException().Message})\n\n" +
                "Pi에서 pi_setup.sh 를 실행했는지, pigpiod가 켜져 있는지 확인하세요.",
                linkLost: true, inner: ex);
        }
    }

    /// <summary>연결을 즉시 끊는다 (오류 뒤 정리용).</summary>
    public void Abort()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    /// <summary>
    /// 명령 하나를 보내고 res 를 돌려준다. 통신 오류/시간 초과는 LinkLost 예외,
    /// pigpio 가 음수 코드로 거부하면 LinkLost=false 예외(연결은 유지).
    /// </summary>
    public int Command(uint cmd, uint p1, uint p2, uint p3 = 0)
    {
        if (!IsConnected)
            throw new ServoException("Pi와의 연결이 끊겼습니다. [연결]을 다시 눌러 주세요.", linkLost: true);

        Span<byte> req = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(req[0..4], cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(req[4..8], p1);
        BinaryPrimitives.WriteUInt32LittleEndian(req[8..12], p2);
        BinaryPrimitives.WriteUInt32LittleEndian(req[12..16], p3);  // 보통 0 (추가 데이터 길이). CF1 보정값 전달 때만 값 사용

        Span<byte> resp = stackalloc byte[16];
        try
        {
            _stream!.Write(req);
            _stream.Flush();

            var got = 0;
            while (got < 16)
            {
                var n = _stream.Read(resp[got..]);
                if (n <= 0)
                    throw new IOException("상대가 연결을 닫았습니다.");
                got += n;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Abort();
            var timedOut = (ex.InnerException ?? ex) is SocketException { SocketErrorCode: SocketError.TimedOut };
            var what = timedOut ? "Pi 응답 없음 (시간 초과)" : "명령 전송 실패";
            throw new ServoException($"{what}: {ex.GetBaseException().Message}", linkLost: true, inner: ex);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(resp[0..4]) != cmd)   // 응답이 요청과 짝이 맞는지
        {
            Abort();
            throw new ServoException("Pi 응답 형식이 올바르지 않습니다.", linkLost: true);
        }

        var result = BinaryPrimitives.ReadInt32LittleEndian(resp[12..16]);
        if (result < 0)
            throw new ServoException(ErrorText(result));
        return result;
    }

    public static string ErrorText(int code) => code switch
    {
        -2 => "사용할 수 없는 GPIO 번호입니다 (PI_BAD_USER_GPIO).",
        -7 => "펄스폭은 0 또는 500~2500 µs 이어야 합니다 (PI_BAD_PULSEWIDTH).",
        -41 => "이 GPIO를 제어할 권한이 없습니다 (PI_NOT_PERMITTED).",
        -93 => "이 GPIO에는 아직 서보 펄스를 설정한 적이 없습니다 (PI_NOT_SERVO_GPIO).",
        _ => $"pigpio 오류 코드 {code}",
    };

    public void Dispose() => Abort();
}
