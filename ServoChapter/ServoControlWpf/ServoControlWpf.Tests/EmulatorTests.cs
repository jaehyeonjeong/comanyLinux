using PigpioEmulator;
using ServoControlWpf.Model;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>가짜 pigpiod(에뮬레이터) + 실제 드라이버/모델을 통째로 연결해 보는 테스트 — Pi 없이 전체 경로 검증</summary>
public class EmulatorTests
{
    private static PigpioEmulatorServer StartServer()
    {
        var s = new PigpioEmulatorServer("127.0.0.1", 0);   // 포트 0 = 빈 포트 자동
        s.Start();
        return s;
    }

    [Fact]
    public void FullPath_Model_Driver_Emulator()
    {
        using var server = StartServer();
        var received = new List<(int gpio, int pulse)>();
        server.PulseChanged += (g, p) => { lock (received) received.Add((g, p)); };

        var driver = new RemotePigpioDriver("127.0.0.1", server.EndPoint.Port, server);
        driver.Open(18);
        Assert.Equal("Pi 리비전 c03111 · pigpio v79", driver.Info);

        var model = new ServoModel(driver);
        model.Start(ServoSpec.Hs311Official());
        model.SetAngle(20);                // → 1700 µs
        model.SetAngle(-40);               // → 1100 µs
        model.Release();                   // → 0

        Assert.Equal(1700, received[0].pulse);
        Assert.Equal(1100, received[1].pulse);
        Assert.Equal(0, received[2].pulse);
        Assert.Equal(0, server.GetPulse(18));
        Assert.True(server.CommandCount >= 5);   // HWVER, PIGPV, SERVO×3
    }

    [Fact]
    public void Emulator_RejectsBadPulse_LikeRealPigpio()
    {
        using var server = StartServer();
        using var c = new PigpioClient("127.0.0.1", server.EndPoint.Port);
        c.Connect();

        var ex = Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdServo, 18, 400));
        Assert.Contains("PI_BAD_PULSEWIDTH", ex.Message);
        var ex2 = Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdServo, 40, 1500));
        Assert.Contains("PI_BAD_USER_GPIO", ex2.Message);
        Assert.Equal(0, c.Command(PigpioClient.CmdServo, 18, 2500));
        Assert.Equal(2500, server.GetPulse(18));
    }

    [Fact]
    public void Emulator_SimulatedServoMovesTowardTarget()
    {
        using var server = StartServer();
        using var c = new PigpioClient("127.0.0.1", server.EndPoint.Port);
        c.Connect();

        c.Command(PigpioClient.CmdServo, 18, 1500);
        Assert.Equal(0, server.GetSimulatedAngle(18), 1);
        c.Command(PigpioClient.CmdServo, 18, 1900);   // 목표 +40°
        var a0 = server.GetSimulatedAngle(18);
        Thread.Sleep(80);
        var a1 = server.GetSimulatedAngle(18);
        Assert.True(a1 > a0, $"서보가 목표를 향해 움직여야 함: {a0} → {a1}");
        Assert.True(a1 <= 40.0);
        Thread.Sleep(300);                            // 0.19 s/60° → 40° 는 약 0.13 s
        Assert.Equal(40.0, server.GetSimulatedAngle(18), 1);
    }
}
