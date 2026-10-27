using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using ServoControlWpf.Model;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 가짜 pigpiod: 루프백 포트에서 16바이트 요청을 받아, 미리 정해 둔 res 로 16바이트 응답을 돌려준다.
/// 실제 Pi 없이도 바이트 열과 오류 처리 흐름을 검증할 수 있다.
/// </summary>
internal sealed class FakePigpiod : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<uint, uint, uint, int> _responder;
    private readonly List<byte[]> _requests = new();
    private readonly CancellationTokenSource _cts = new();

    public FakePigpiod(Func<uint, uint, uint, int> responder, bool answer = true)
    {
        _responder = responder;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => ServeAsync(answer));
    }

    public int Port { get; }
    public IReadOnlyList<byte[]> Requests { get { lock (_requests) return _requests.ToArray(); } }

    private async Task ServeAsync(bool answer)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();
                var buf = new byte[16];
                while (true)
                {
                    var got = 0;
                    while (got < 16)
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(got, 16 - got), _cts.Token);
                        if (n == 0) goto next;
                        got += n;
                    }
                    lock (_requests) _requests.Add(buf.ToArray());
                    if (!answer) continue;   // 응답하지 않음 → 클라이언트 시간 초과 테스트

                    var cmd = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
                    var p1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
                    var p2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(8, 4));
                    var resp = new byte[16];
                    buf.AsSpan(0, 12).CopyTo(resp);
                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(12, 4), _responder(cmd, p1, p2));
                    await stream.WriteAsync(resp, _cts.Token);
                }
            next:;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

public class PigpioClientTests
{
    [Fact]
    public void Servo_SendsExactly16BytesLittleEndian()
    {
        using var pi = new FakePigpiod((_, _, _) => 0);
        using var c = new PigpioClient("127.0.0.1", pi.Port);
        c.Connect();

        var res = c.Command(PigpioClient.CmdServo, 18, 1500);

        Assert.Equal(0, res);
        var req = Assert.Single(pi.Requests);
        // "GPIO18을 1500 µs로" → 08 00 00 00 · 12 00 00 00 · dc 05 00 00 · 00 00 00 00
        Assert.Equal(new byte[] { 0x08, 0, 0, 0, 0x12, 0, 0, 0, 0xdc, 0x05, 0, 0, 0, 0, 0, 0 }, req);
    }

    [Fact]
    public void NegativeResult_ThrowsKoreanMessage_KeepsLink()
    {
        using var pi = new FakePigpiod((cmd, _, p2) => cmd == PigpioClient.CmdServo && p2 == 3000 ? -7 : 0);
        using var c = new PigpioClient("127.0.0.1", pi.Port);
        c.Connect();

        var ex = Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdServo, 18, 3000));

        Assert.Contains("PI_BAD_PULSEWIDTH", ex.Message);
        Assert.False(ex.LinkLost);
        Assert.True(c.IsConnected);          // pigpio 가 거부한 것이므로 연결은 유지
        Assert.Equal(0, c.Command(PigpioClient.CmdServo, 18, 1500));
    }

    [Fact]
    public void NoResponse_TimesOut_AsLinkLost()
    {
        using var pi = new FakePigpiod((_, _, _) => 0, answer: false);
        using var c = new PigpioClient("127.0.0.1", pi.Port, timeoutMs: 300);
        c.Connect();

        var ex = Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdHwVer, 0, 0));

        Assert.True(ex.LinkLost);
        Assert.False(c.IsConnected);
        Assert.Contains("시간 초과", ex.Message);
    }

    [Fact]
    public void Connect_Refused_IsLinkLostWithHint()
    {
        // 아무도 듣지 않는 포트 찍기
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var c = new PigpioClient("127.0.0.1", port, timeoutMs: 1000);
        var ex = Assert.Throws<ServoException>(() => c.Connect());
        Assert.True(ex.LinkLost);
        Assert.Contains("pi_setup.sh", ex.Message);
    }

    [Fact]
    public void RemoteDriver_Open_ReadsHwVerAndPigpv_AndReleaseSendsZero()
    {
        using var pi = new FakePigpiod((cmd, _, _) => cmd switch
        {
            PigpioClient.CmdHwVer => 0xc03111,
            PigpioClient.CmdPigpv => 79,
            _ => 0,
        });
        var d = new RemotePigpioDriver("127.0.0.1", pi.Port);
        d.Open(18);
        Assert.Equal("Pi 리비전 c03111 · pigpio v79", d.Info);
        Assert.True(d.IsLinkAlive);

        d.WritePulse(1700);
        d.Dispose();   // → Release → SERVO(18, 0)

        var servoCmds = pi.Requests.Where(r => r[0] == 8).ToList();
        Assert.Equal(2, servoCmds.Count);
        Assert.Equal(1700u, BinaryPrimitives.ReadUInt32LittleEndian(servoCmds[0].AsSpan(8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(servoCmds[1].AsSpan(8, 4)));
    }

    [Fact]
    public void ErrorText_KnownCodes()
    {
        Assert.Contains("PI_BAD_USER_GPIO", PigpioClient.ErrorText(-2));
        Assert.Contains("PI_NOT_PERMITTED", PigpioClient.ErrorText(-41));
        Assert.Equal("pigpio 오류 코드 -99", PigpioClient.ErrorText(-99));
    }
}
