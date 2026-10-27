using ServoControlWpf.Model;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>펄스를 기록만 하는 가짜 드라이버. FailNext 로 다음 WritePulse 를 실패시킬 수 있다.</summary>
internal sealed class RecordingDriver : IServoDriver
{
    public List<int> Pulses { get; } = new();
    public List<int> OpenedGpios { get; } = new();
    public int ReleaseCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailNextWrite { get; set; }
    public bool FailOpen { get; set; }
    /// <summary>ReadPulse 가 돌려줄 "장치의 현재 펄스" (null = 보고 없음)</summary>
    public int? PulseToReport { get; set; }

    public string Name => "recording";
    public string Info => "";
    public bool IsRemote { get; set; } = true;
    public bool IsLinkAlive => true;
    public bool ReleaseOnDispose { get; set; } = true;
    public int? ReadPulse() => PulseToReport;

    public void Open(int gpio)
    {
        if (FailOpen) throw new ServoException("open 실패");
        OpenedGpios.Add(gpio);
    }

    public void WritePulse(int microseconds)
    {
        if (FailNextWrite) { FailNextWrite = false; throw new ServoException("write 실패"); }
        Pulses.Add(microseconds);
    }

    public void Release() => ReleaseCount++;
    public void Dispose() => Disposed = true;
}

public class ServoModelTests
{
    [Fact]
    public void SetAngle_WritesPulse_AndRaisesChanged()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());
        var changed = 0;
        m.Changed += (_, _) => changed++;

        m.SetAngle(20);

        Assert.Equal(new[] { 1700 }, drv.Pulses);
        Assert.Equal(20, m.Angle);
        Assert.Equal(1700, m.Pulse);
        Assert.True(m.IsOutputOn);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void SetAngle_Failure_LeavesStateUnchanged()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(10);

        drv.FailNextWrite = true;
        Assert.Throws<ServoException>(() => m.SetAngle(30));

        Assert.Equal(10, m.Angle);
        Assert.True(m.IsOutputOn);
    }

    [Fact]
    public void SetSpec_Invalid_Rejected()
    {
        var m = new ServoModel(new RecordingDriver());
        m.Start(ServoSpec.Hs311Official());
        var ex = Assert.Throws<ServoException>(() => m.SetSpec(new ServoSpec(1500, 1500, 1900, -40, 40, 18)));
        Assert.Contains("보정값", ex.Message);
        Assert.Equal(ServoSpec.Hs311Official(), m.Spec);
    }

    [Fact]
    public void SetSpec_WriteFails_KeepsOldSpec()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(20);                         // 출력 켜짐 → 새 사양으로 먼저 써 봄

        drv.FailNextWrite = true;
        Assert.Throws<ServoException>(() => m.SetSpec(ServoSpec.Hs311Extended()));

        Assert.Equal(ServoSpec.Hs311Official(), m.Spec);
        Assert.Equal(20, m.Angle);
    }

    [Fact]
    public void SetSpec_Success_ClampsAngle_AndRewrites()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Extended());
        m.SetAngle(60);                         // 확장 범위에서 60°

        m.SetSpec(ServoSpec.Hs311Official());   // 공식 범위로 좁히면 40° 로 잘려 다시 출력

        Assert.Equal(40, m.Angle);
        Assert.Equal(1900, drv.Pulses[^1]);
    }

    [Fact]
    public void SetSpec_GpioChange_ReleasesAndReopens()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());

        m.SetSpec(ServoSpec.Hs311Official() with { Gpio = 12 });

        Assert.Equal(1, drv.ReleaseCount);
        Assert.Equal(new[] { 12 }, drv.OpenedGpios);
        Assert.Equal(12, m.Spec.Gpio);
        Assert.False(m.IsOutputOn);
    }

    [Fact]
    public void SetDriver_ReleasesAndDisposesOld()
    {
        var old = new RecordingDriver();
        var m = new ServoModel(old);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(5);

        var fresh = new RecordingDriver();
        m.SetDriver(fresh);

        Assert.Equal(1, old.ReleaseCount);
        Assert.True(old.Disposed);
        Assert.False(m.IsOutputOn);
        Assert.Equal("recording", m.DriverName);
    }

    [Fact]
    public void SetDriver_DeviceReportsPulse_SyncsAngleAndOutput()
    {
        // 연결한 장치가 이미 1700 µs 로 구동 중 → 화면은 +20°, 출력 켜짐으로 맞춰져야 함
        var m = new ServoModel(new SimulatedDriver());
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(-30);                                   // 시뮬레이션에서 다이얼을 돌려 둔 상태

        var dev = new RecordingDriver { PulseToReport = 1700 };
        m.SetDriver(dev);

        Assert.Equal(20.0, m.Angle, 3);
        Assert.Equal(1700, m.Pulse);
        Assert.True(m.IsOutputOn);
        Assert.Equal(1700, m.DevicePulseAtConnect);
        Assert.Empty(dev.Pulses);                          // 맞추기만 했고 명령은 보내지 않음
    }

    [Fact]
    public void SetDriver_KeepOutput_DoesNotReleaseOld_AndRemembersAngle()
    {
        var old = new RecordingDriver();
        var m = new ServoModel(old);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(-20);                                   // 실제 장치로 보낸 마지막 각도

        var sim = new SimulatedDriver();
        m.SetDriver(sim, releaseOld: false);               // "연결 해제 시 출력 유지"

        Assert.Equal(0, old.ReleaseCount);
        Assert.False(old.ReleaseOnDispose);
        Assert.True(old.Disposed);
        Assert.True(m.IsOutputOn);                         // 서보는 여전히 펄스를 받고 있음
        Assert.Equal(-20.0, m.LastDrivenAngle);

        m.SetAngle(40);                                    // 해제 상태에서 다이얼을 돌림 (시뮬레이션) → 기억은 바뀌지 않음
        Assert.Equal(-20.0, m.LastDrivenAngle);

        var dev = new RecordingDriver { PulseToReport = 1300 };   // 다시 연결: 장치가 1300 µs(−20°) 유지 중이라고 보고
        m.SetDriver(dev);
        Assert.Equal(-20.0, m.Angle, 3);
        Assert.True(m.IsOutputOn);
    }

    [Fact]
    public void SetDriver_DeviceReportsZero_RestoresLastDrivenAngle_OutputOff()
    {
        var old = new RecordingDriver();
        var m = new ServoModel(old);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(-20);
        m.SetDriver(new SimulatedDriver());                // 출력 끄고 해제
        m.SetAngle(40);                                    // 해제 상태에서 다이얼만 돌림

        var dev = new RecordingDriver { PulseToReport = 0 };     // 다시 연결: 출력 꺼짐 보고
        m.SetDriver(dev);

        Assert.Equal(-20.0, m.Angle, 3);                   // 서보가 실제로 멈춰 있는 마지막 위치로 되돌림
        Assert.False(m.IsOutputOn);
    }

    [Fact]
    public void Dispose_Default_Releases_ButKeepOptionSkipsRelease()
    {
        var drv = new RecordingDriver { ReleaseOnDispose = false };
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());
        m.Dispose();                                       // 기본: 종료 때 출력 끔
        Assert.True(drv.ReleaseOnDispose);
        Assert.True(drv.Disposed);

        var drv2 = new RecordingDriver();
        var m2 = new ServoModel(drv2);
        m2.Start(ServoSpec.Hs311Official());
        m2.SetAngle(15);
        m2.Dispose(releaseOutput: false);                  // 출력 유지 옵션: 접속만 끊고 서보는 자리를 지킴
        Assert.False(drv2.ReleaseOnDispose);
        Assert.True(drv2.Disposed);
        Assert.True(m2.IsOutputOn);
    }

    [Fact]
    public void Start_WithSavedAngle_BeginsThere()
    {
        // 이전 실행에서 저장한 각도(−25°)로 화면이 시작하고, 출력은 꺼진 상태
        var m = new ServoModel(new SimulatedDriver());
        m.Start(ServoSpec.Hs311Official(), lastDrivenAngle: -25);
        Assert.Equal(-25.0, m.Angle);
        Assert.Equal(-25.0, m.LastDrivenAngle);
        Assert.False(m.IsOutputOn);

        var m2 = new ServoModel(new SimulatedDriver());
        m2.Start(ServoSpec.Hs311Official(), lastDrivenAngle: 70);   // 범위 밖 저장값은 잘림
        Assert.Equal(40.0, m2.Angle);
    }

    [Fact]
    public void Release_TurnsOutputOff()
    {
        var drv = new RecordingDriver();
        var m = new ServoModel(drv);
        m.Start(ServoSpec.Hs311Official());
        m.SetAngle(5);

        m.Release();

        Assert.Equal(1, drv.ReleaseCount);
        Assert.False(m.IsOutputOn);
        Assert.Equal(5, m.Angle);   // 각도 표시는 유지
    }
}
