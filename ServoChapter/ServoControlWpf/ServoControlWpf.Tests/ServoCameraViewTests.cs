using ServoControlWpf.View;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 왼쪽 카메라 뷰는 "연결됨 + 출력 켜짐" 일 때만 움직인다.
/// 연결 해제 상태에서 다이얼·각도·펄스를 바꿔도 회전 애니메이션이 시작되지 않아야 한다.
/// (WPF 요소는 STA 스레드에서만 만들 수 있다)
/// </summary>
public class ServoCameraViewTests
{
    private static void OnSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    [Fact]
    public void Disconnected_AngleChangesDoNotStartAnimation()
    {
        OnSta(() =>
        {
            var v = new ServoCameraView { IsConnected = false, IsOutputOn = true };
            v.Angle = 30;
            v.Angle = -20;
            Assert.False(v.IsAnimating);
            Assert.Equal(0.0, v.ShownAngle);     // 그려진 카메라는 제자리
        });
    }

    [Fact]
    public void Connected_WithOutputOn_StartsAnimationTowardTarget()
    {
        OnSta(() =>
        {
            var v = new ServoCameraView { IsConnected = true, IsOutputOn = true };
            v.Angle = 30;
            Assert.True(v.IsAnimating);
            Assert.True(v.IsMoving);
        });
    }

    [Fact]
    public void Connected_WithOutputOff_DoesNotMove()
    {
        OnSta(() =>
        {
            var v = new ServoCameraView { IsConnected = true, IsOutputOn = false };
            v.Angle = 30;
            Assert.False(v.IsAnimating);
            v.IsOutputOn = true;                 // 첫 명령으로 출력이 켜지면 그때 목표를 향해 출발
            Assert.True(v.IsAnimating);
        });
    }

    [Fact]
    public void Connecting_WhileDriven_SnapsToCurrentAngleWithoutAnimation()
    {
        OnSta(() =>
        {
            // 해제 상태에서 카메라는 0° 에 멈춰 있고, 연결 직후 모델이 장치 보고(GPW)로 −20° 를 알려 준 상황
            var v = new ServoCameraView { IsConnected = false, IsOutputOn = true };
            v.Angle = -20;
            Assert.Equal(0.0, v.ShownAngle);
            v.IsConnected = true;                // 서보는 이미 거기 있으므로 회전 없이 바로 맞춤
            Assert.Equal(-20.0, v.ShownAngle);
            Assert.False(v.IsAnimating);
        });
    }

    [Fact]
    public void Disconnecting_StopsAnimationInPlace()
    {
        OnSta(() =>
        {
            var v = new ServoCameraView { IsConnected = true, IsOutputOn = true };
            v.Angle = 30;
            Assert.True(v.IsAnimating);
            v.IsConnected = false;               // 연결 해제 → 그 자리에서 정지
            Assert.False(v.IsAnimating);
            v.Angle = -40;                       // 해제 상태에서 값을 바꿔도 움직이지 않음
            Assert.False(v.IsAnimating);
            Assert.Equal(0.0, v.ShownAngle);
        });
    }
}
