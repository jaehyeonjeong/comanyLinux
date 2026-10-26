using ServoControlWpf.Model;
using Xunit;

namespace ServoControlWpf.Tests;

public class ServoSpecTests
{
    [Theory]
    [InlineData(0.0, 1500)]
    [InlineData(20.0, 1700)]     // 1500 + 20 × 400 / 40
    [InlineData(40.0, 1900)]
    [InlineData(-40.0, 1100)]
    [InlineData(-10.0, 1400)]
    [InlineData(90.0, 1900)]     // 범위 밖은 최대로 잘림
    [InlineData(-90.0, 1100)]
    public void AngleToPulse_Official(double angle, int expected)
    {
        var spec = ServoSpec.Hs311Official();
        Assert.Equal(expected, spec.AngleToPulse(angle));
    }

    [Fact]
    public void AngleToPulse_AsymmetricCalibration()
    {
        // 양쪽 기울기가 다른 보정값: −쪽 400 µs/40°, +쪽 300 µs/40°
        var spec = new ServoSpec(1100, 1500, 1800, -40, 40, 18);
        Assert.Equal(1650, spec.AngleToPulse(20));
        Assert.Equal(1300, spec.AngleToPulse(-20));
    }

    [Fact]
    public void PulseToAngle_RoundTrip()
    {
        var spec = ServoSpec.Hs311Extended();
        foreach (var a in new[] { -90.0, -45.0, 0.0, 30.0, 90.0 })
            Assert.Equal(a, spec.PulseToAngle(spec.AngleToPulse(a)), 3);
    }

    [Fact]
    public void Defaults_AreOfficialHs311()
    {
        var s = new ServoSpec();
        Assert.Equal(1100, s.MinPulse);
        Assert.Equal(1500, s.NeutralPulse);
        Assert.Equal(1900, s.MaxPulse);
        Assert.Equal(-40, s.MinAngle);
        Assert.Equal(40, s.MaxAngle);
        Assert.Equal(18, s.Gpio);
        Assert.True(s.IsValid());
        Assert.True(ServoSpec.Hs311Extended().IsValid());
    }

    [Theory]
    [InlineData(400, 1500, 1900, -40, 40, 18)]    // 최소 < 500
    [InlineData(1100, 1500, 2600, -40, 40, 18)]   // 최대 > 2500
    [InlineData(1500, 1500, 1900, -40, 40, 18)]   // 최소 == 중립
    [InlineData(1100, 1500, 1900, 0, 40, 18)]     // 최소각 0
    [InlineData(1100, 1500, 1900, -40, 91, 18)]   // 최대각 > 90
    [InlineData(1100, 1500, 1900, -40, 40, 1)]    // GPIO < 2
    [InlineData(1100, 1500, 1900, -40, 40, 28)]   // GPIO > 27
    public void IsValid_RejectsBadValues(int min, int neutral, int max, double minA, double maxA, int gpio)
    {
        Assert.False(new ServoSpec(min, neutral, max, minA, maxA, gpio).IsValid());
    }
}
