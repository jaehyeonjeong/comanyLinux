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

    public string Name => "recording";
    public string Info => "";
    public bool IsRemote => true;
    public bool IsLinkAlive => true;

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
