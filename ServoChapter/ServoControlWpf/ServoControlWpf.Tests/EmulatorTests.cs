using System.IO;
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
    public void Calibration_ChangesSimulatedAngle_NotPulse()
    {
        // 사용자 보정 600/1500/2400, ±80°: 600 µs 는 WPF 에서 −80° 인데 가짜 Pi 가 공식값(10 µs/°)으로 −90° 라고 하면 안 맞음
        using var server = StartServer();
        using var c = new PigpioClient("127.0.0.1", server.EndPoint.Port);
        c.Connect();
        Assert.Equal(0, c.Command(PigpioClient.CmdServo, 18, 600));
        Thread.Sleep(400);                                            // 0.19 s/60° → 90° 는 0.29 s
        Assert.Equal(-90.0, server.GetSimulatedAngle(18), 1);         // 기본 = HS-311 공식값

        var spec = new ServoSpec(600, 1500, 2400, -80, 80, 18);
        server.SetCalibration(spec.ToCalibration());
        Assert.Equal(600, server.GetPulse(18));                       // 펄스는 그대로
        Assert.Equal(-80.0, server.GetSimulatedAngle(18), 1);         // 각도만 WPF 공식으로
        Assert.Equal("600,1500,2400,-80,80", server.Calibration.ToCalibString());
        Assert.Equal(spec.PulseToAngle(2000), server.Calibration.PulseToAngle(2000), 6);
    }

    [Fact]
    public void Calibration_CanBeSentOverTheWire_WithCf1()
    {
        using var server = StartServer();
        var received = new List<string>();
        server.CommandReceived += ev => { lock (received) received.Add(ev.Description); };
        using var c = new PigpioClient("127.0.0.1", server.EndPoint.Port);
        c.Connect();
        Assert.True(c.SendCalibration(new ServoSpec(600, 1500, 2400, -80, 80, 18).ToCalibration()));
        Assert.Equal("600,1500,2400,-80,80", server.Calibration.ToCalibString());
        // CommandReceived 는 응답을 보낸 뒤 올라오므로 잠깐 기다림
        for (var i = 0; i < 50; i++)
        {
            lock (received) if (received.Count >= 2) break;
            Thread.Sleep(20);
        }
        lock (received)
        {
            Assert.Contains(received, d => d.Contains("CALIB 펄스 600/1500/2400"));
            Assert.Contains(received, d => d.Contains("CALIB 각도 -80°~80°"));
        }
        // 잘못된 값은 −7 로 거부(클라이언트는 음수 응답을 ServoException 으로 올림)하고 이전 값 유지
        Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdCustom1, PigpioEmulator.ServoCalibration.SubPulses, 2000u | (1500u << 16), 1900));
        Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdCustom1, PigpioEmulator.ServoCalibration.SubAngles, unchecked((uint)-950), 800));
        Assert.Equal("600,1500,2400,-80,80", server.Calibration.ToCalibString());
        // 소수 각도(−40.5°)도 1/10 단위로 왕복
        Assert.True(c.SendCalibration(new PigpioEmulator.ServoCalibration(1100, 1500, 1900, -40.5, 40)));
        Assert.Equal("1100,1500,1900,-40.5,40", server.Calibration.ToCalibString());
    }

    [Fact]
    public void Calibration_IsKeptInStateFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "emu-calib-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var s1 = new PigpioEmulatorServer("127.0.0.1", 0, file))
            {
                s1.Start();
                s1.SetCalibration(new PigpioEmulator.ServoCalibration(600, 1500, 2400, -80, 80));
                using var c = new PigpioClient("127.0.0.1", s1.EndPoint.Port);
                c.Connect();
                c.Command(PigpioClient.CmdServo, 18, 600);
            }
            using var s2 = new PigpioEmulatorServer("127.0.0.1", 0, file);
            s2.Start();
            Assert.Equal("600,1500,2400,-80,80", s2.Calibration.ToCalibString());
            Assert.Equal(600, s2.GetPulse(18));
            Assert.Equal(-80.0, s2.GetSimulatedAngle(18), 1);          // 되살릴 때도 보정값 기준
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void OldStateFileFormat_StillLoads()
    {
        var file = Path.Combine(Path.GetTempPath(), "emu-old-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, "{\"18\":{\"Pulse\":1700,\"Angle\":20.0}}");     // v1.3.0 까지의 모양
            using var s = new PigpioEmulatorServer("127.0.0.1", 0, file);
            s.Start();
            Assert.Equal(1700, s.GetPulse(18));
            Assert.Equal(20.0, s.GetSimulatedAngle(18), 1);
            Assert.Equal(PigpioEmulator.ServoCalibration.Default, s.Calibration);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
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
    public void Reconnect_RestoresRememberedAngle_FromEmulator()
    {
        // 연결 → −20° → "출력 유지" 로 해제 → 해제 중 다이얼 40° → 다시 연결 → 에뮬레이터가 기억한 1300 µs(−20°)로 화면이 맞춰짐
        using var server = StartServer();
        var port = server.EndPoint.Port;

        var d1 = new RemotePigpioDriver("127.0.0.1", port);
        d1.Open(18);
        var model = new ServoModel(d1);
        model.Start(ServoSpec.Hs311Official());
        model.SetAngle(-20);                                // → 1300 µs
        Assert.Equal(1300, d1.ReadPulse());                 // GPW 가 지금 펄스를 돌려줌

        model.SetDriver(new SimulatedDriver(), releaseOld: false);   // 접속만 끊음
        Assert.Equal(1300, server.GetPulse(18));            // 서버는 펄스를 그대로 기억
        model.SetAngle(40);                                 // 해제 상태의 조작은 서버에 가지 않음
        Assert.Equal(1300, server.GetPulse(18));

        var d2 = new RemotePigpioDriver("127.0.0.1", port);
        d2.Open(18);
        model.SetDriver(d2);
        Assert.Equal(-20.0, model.Angle, 3);
        Assert.Equal(1300, model.Pulse);
        Assert.True(model.IsOutputOn);
        Assert.Equal(1300, model.DevicePulseAtConnect);

        // 출력을 끄고 해제하면 GPW = 0 → 마지막으로 보낸 각도로 되돌리고 출력 꺼짐 표시
        model.SetDriver(new SimulatedDriver(), releaseOld: true);
        Assert.Equal(0, server.GetPulse(18));
        model.SetAngle(10);
        var d3 = new RemotePigpioDriver("127.0.0.1", port);
        d3.Open(18);
        model.SetDriver(d3);
        Assert.Equal(-20.0, model.Angle, 3);
        Assert.False(model.IsOutputOn);
        model.Dispose();
    }

    [Fact]
    public void StateFile_SurvivesRestart_LikeARealServo()
    {
        // 프로그램을 껐다 켠 상황: 서버를 끄고(상태 파일 저장) 새 서버를 같은 파일로 띄우면 펄스·각도가 그대로
        var file = Path.Combine(Path.GetTempPath(), "pigpio-emu-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var s1 = new PigpioEmulatorServer("127.0.0.1", 0, file))
            {
                s1.Start();
                using var c = new PigpioClient("127.0.0.1", s1.EndPoint.Port);
                c.Connect();
                c.Command(PigpioClient.CmdServo, 18, 1250);   // −25°
                Thread.Sleep(250);
            }                                                 // Dispose → 저장
            Assert.True(File.Exists(file));

            using var s2 = new PigpioEmulatorServer("127.0.0.1", 0, file);
            s2.Start();                                       // Start → 복원
            Assert.Equal(1250, s2.GetPulse(18));
            Assert.Equal(-25.0, s2.GetSimulatedAngle(18), 1);

            // 새로 켠 프로그램이 연결하면 GPW 로 1250 을 받아 화면을 −25° 에 맞춘다
            var d = new RemotePigpioDriver("127.0.0.1", s2.EndPoint.Port);
            d.Open(18);
            var model = new ServoModel(new SimulatedDriver());
            model.Start(ServoSpec.Hs311Official());
            model.SetDriver(d);
            Assert.Equal(-25.0, model.Angle, 3);
            Assert.True(model.IsOutputOn);
            model.Dispose(releaseOutput: false);

            // 출력을 끄고 종료한 경우: 펄스 0 이지만 흉내 낸 각도는 남아 있어야 함
            using (var c2 = new PigpioClient("127.0.0.1", s2.EndPoint.Port))
            {
                c2.Connect();
                c2.Command(PigpioClient.CmdServo, 18, 0);
            }
            s2.Dispose();
            using var s3 = new PigpioEmulatorServer("127.0.0.1", 0, file);
            s3.Start();
            Assert.Equal(0, s3.GetPulse(18));
            Assert.Equal(-25.0, s3.GetSimulatedAngle(18), 1);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Gpw_OnUnusedGpio_IsNotServoGpio()
    {
        using var server = StartServer();
        using var c = new PigpioClient("127.0.0.1", server.EndPoint.Port);
        c.Connect();
        var ex = Assert.Throws<ServoException>(() => c.Command(PigpioClient.CmdGetServoPulsewidth, 18, 0));
        Assert.Contains("PI_NOT_SERVO_GPIO", ex.Message);
        Assert.False(ex.LinkLost);

        var d = new RemotePigpioDriver("127.0.0.1", server.EndPoint.Port);
        d.Open(18);
        Assert.Null(d.ReadPulse());                         // 드라이버는 거부 응답을 "알 수 없음" 으로
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
