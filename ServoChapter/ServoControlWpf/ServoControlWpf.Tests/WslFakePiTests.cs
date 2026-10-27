using System.Net;
using System.Net.Sockets;
using ServoControlWpf.Model;
using ServoControlWpf.Services;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 에뮬레이터 종류 "WSL 가짜 Pi": 실제 wsl.exe 로 tools/fake_pigpiod.py 를 띄워 16바이트 규약이 가상 네트워크를 건너는지.
/// WSL 이 없는 PC 에서는 조용히 통과한다.
/// </summary>
public class WslFakePiTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    [Fact]
    public void FindScript_LocatesToolsFakePigpiod()
    {
        var s = WslFakePi.FindScript();
        Assert.NotNull(s);
        Assert.EndsWith("fake_pigpiod.py", s);
    }

    [Fact]
    public async Task Open_StartsFakePi_InWsl_AndDisposeStopsIt()
    {
        var probe = await WslService.ProbeAsync();
        if (!probe.Available) return;                       // WSL 없는 PC

        var port = FreePort();
        var lines = new List<string>();
        var fp = await WslFakePi.OpenAsync(null, port, timeoutMs: 20000);
        try
        {
            Assert.True(fp.OwnsProcess);
            fp.LineReceived += l => { lock (lines) lines.Add(l); };
            using (var c = new PigpioClient("127.0.0.1", port))
            {
                c.Connect();
                Assert.Equal(79, c.Command(PigpioClient.CmdPigpv, 0, 0));
                Assert.Equal(0, c.Command(PigpioClient.CmdServo, 18, 1700));
                Assert.Equal(1700, c.Command(PigpioClient.CmdGetServoPulsewidth, 18, 0));
            }
            await Task.Delay(500);
            lock (lines) Assert.Contains(lines, l => l.Contains("SERVO gpio=18 1700"));
            Assert.Contains("fake_pigpiod.py", fp.Describe());
        }
        finally
        {
            fp.Dispose();
        }
        // 종료 뒤 포트가 닫혀야 함
        var closed = false;
        for (var i = 0; i < 30 && !closed; i++)
        {
            await Task.Delay(200);
            using var t = new TcpClient();
            try { await t.ConnectAsync("127.0.0.1", port); } catch (SocketException) { closed = true; }
        }
        Assert.True(closed, "Dispose 뒤 fake_pigpiod.py 가 종료되어 포트가 닫혀야 함");
    }

    [Fact]
    public async Task Open_ReusesExistingServer_OnThatPort()
    {
        // 그 포트에 이미 서버가 있으면 새로 띄우지 않고 그대로 사용 (OwnsProcess=false)
        using var server = new PigpioEmulator.PigpioEmulatorServer("127.0.0.1", 0);
        server.Start();
        var fp = await WslFakePi.OpenAsync(null, server.EndPoint.Port);
        Assert.False(fp.OwnsProcess);
        Assert.Contains("이미 떠 있던", fp.Describe());
        fp.Dispose();
    }
}
