using System.Windows;
using ServoControlWpf.View;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>다이얼의 "마우스 위치 → 값" 계산. WPF 요소는 STA 스레드에서만 만들 수 있어 전용 스레드에서 돌린다.</summary>
public class ServoDialTests
{
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    private static ServoDial MakeDial(double min = -40, double max = 40, double size = 300)
    {
        var d = new ServoDial { Minimum = min, Maximum = max, Width = size, Height = size };
        d.Measure(new Size(size, size));
        d.Arrange(new Rect(0, 0, size, size));
        return d;
    }

    private static Point At(double size, double thetaDeg, double r = 100)
    {
        var rad = thetaDeg * Math.PI / 180;
        return new Point(size / 2 + r * Math.Sin(rad), size / 2 - r * Math.Cos(rad));
    }

    [Theory]
    [InlineData(0.0, 0.0)]        // 위 = 0°
    [InlineData(135.0, 40.0)]     // 오른쪽 아래 끝 = 최대
    [InlineData(-135.0, -40.0)]   // 왼쪽 아래 끝 = 최소
    [InlineData(45.0, 13.0)]      // 45/135 × 40 = 13.3 → 1° 단위로 13
    [InlineData(-67.5, -20.0)]
    [InlineData(170.0, 40.0)]     // 아래쪽 빈 구간은 가까운 끝(최대)으로
    [InlineData(-170.0, -40.0)]
    public void ValueFromPoint_MapsDirectionToAngle(double theta, double expected)
    {
        var v = OnSta(() => MakeDial().ValueFromPoint(At(300, theta)));
        Assert.Equal(expected, v, 6);
    }

    [Fact]
    public void ValueFromPoint_UsesCurrentRange()
    {
        // 확장 범위 ±90: 오른쪽(90°) 은 90/135 × 90 = 60
        var v = OnSta(() => MakeDial(-90, 90).ValueFromPoint(At(300, 90)));
        Assert.Equal(60.0, v, 6);
    }

    [Fact]
    public void Snap_RoundsToStepAndClamps()
    {
        var (a, b, c) = OnSta(() =>
        {
            var d = MakeDial();
            return (d.Snap(12.4), d.Snap(12.6), d.Snap(55));
        });
        Assert.Equal(12.0, a);
        Assert.Equal(13.0, b);
        Assert.Equal(40.0, c);
    }
}
