using System.Windows;
using System.Windows.Controls;
using ServoControlWpf.View;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 긴 글이 잘리지 않게 글꼴을 자동으로 줄이는 ShrinkTextBlock. WPF 요소라 STA 스레드에서 실제 레이아웃(Measure/Arrange)을 돌려 확인한다.
/// </summary>
public class ShrinkTextBlockTests
{
    private static T Sta<T>(Func<T> f)
    {
        T result = default!; Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    private const string Long = "WSL 가짜 Pi: 10:22:26  ✓ SERVO gpio=18 1750 µs → 목표 +25.0° (이미 떠 있으면 그것 사용)";

    private static void DoLayout(FrameworkElement host, double width)
    {
        host.Width = width;
        host.Measure(new Size(width, 40)); host.Arrange(new Rect(0, 0, width, 40)); host.UpdateLayout();
    }

    /// <summary>폭이 정해진 Grid 에 넣고 레이아웃 (DockPanel 채우기처럼 폭을 꽉 채우는 상황)</summary>
    private static (ShrinkTextBlock tb, Grid host) Layout(double width, string text = Long)
    {
        var tb = new ShrinkTextBlock { Text = text, FontSize = 15, MinFontSize = 10 };
        var host = new Grid { Height = 40 };
        host.Children.Add(tb);
        DoLayout(host, width);
        return (tb, host);
    }

    [Fact]
    public void WideEnough_KeepsFontSize_NoToolTip()
    {
        var (size, trimmed, tip) = Sta(() => { var (tb, _) = Layout(2000); return (tb.EffectiveFontSize, tb.IsTrimmed, tb.ToolTip); });
        Assert.Equal(15, size);
        Assert.False(trimmed);
        Assert.Null(tip);
    }

    [Fact]
    public void Narrow_ShrinksFont_UntilItFits()
    {
        var (size, trimmed, desired, width) = Sta(() =>
        {
            var (wide, _) = Layout(2000);
            var natural = wide.DesiredSize.Width;
            var (tb, _) = Layout(natural * 0.8);                 // 80% 폭 → 글꼴을 줄이면 들어감
            return (tb.EffectiveFontSize, tb.IsTrimmed, tb.DesiredSize.Width, natural * 0.8);
        });
        Assert.True(size is >= 10 and <= 14.5, $"size={size}");
        Assert.False(trimmed);
        Assert.True(desired <= width + 0.5, $"desired={desired} width={width}");
    }

    [Fact]
    public void TooNarrow_StopsAtMin_TrimsAndShowsToolTip()
    {
        var (size, trimmed, tip, aw) = Sta(() => { var (tb, _) = Layout(120); return (tb.EffectiveFontSize, tb.IsTrimmed, tb.ToolTip as string, tb.ActualWidth); });
        Assert.True(size == 10, $"size={size} trimmed={trimmed} actual={aw}");
        Assert.True(trimmed);
        Assert.Equal(Long, tip);
        Assert.InRange(aw, 119, 121);
    }

    [Fact]
    public void GrowsBack_WhenSpaceReturns()
    {
        var (size, tip) = Sta(() =>
        {
            var (tb, host) = Layout(120);
            DoLayout(host, 2000);
            return (tb.EffectiveFontSize, tb.ToolTip);
        });
        Assert.Equal(15, size);
        Assert.Null(tip);
    }

    [Fact]
    public void ShortText_AfterLongText_RestoresFontSize()
    {
        var size = Sta(() =>
        {
            var (tb, host) = Layout(300);                        // 긴 글 → 줄어듦
            tb.Text = "짧은 글";
            host.UpdateLayout();
            return tb.EffectiveFontSize;
        });
        Assert.Equal(15, size);
    }

    [Fact]
    public void MaxWidth_IsTheBudget_InHorizontalStack()
    {
        // 가로 StackPanel 은 자식에게 폭 제한을 주지 않으므로(무한) MaxWidth 가 예산 노릇을 한다
        var (size, width) = Sta(() =>
        {
            var tb = new ShrinkTextBlock { Text = Long, FontSize = 15, MinFontSize = 10, MaxWidth = 300 };
            var host = new StackPanel { Orientation = Orientation.Horizontal, Height = 40 };
            host.Children.Add(tb);
            DoLayout(host, 1000);
            return (tb.EffectiveFontSize, tb.ActualWidth);
        });
        Assert.True(size is >= 10 and <= 14.5, $"size={size}");
        Assert.InRange(width, 1, 300.5);
    }

    [Fact]
    public void ExplicitToolTip_IsNotOverwritten()
    {
        var tip = Sta(() =>
        {
            var tb = new ShrinkTextBlock { Text = Long, FontSize = 15, ToolTip = "내가 쓴 설명" };
            var host = new Grid { Height = 40 };
            host.Children.Add(tb);
            DoLayout(host, 120);
            return tb.ToolTip as string;
        });
        Assert.Equal("내가 쓴 설명", tip);
    }

    [Fact]
    public void InnerTextBlock_ExposesText_ForAutomation()
    {
        // UI 자동화·화면 읽기 프로그램은 안쪽 TextBlock(ControlType Text)으로 글을 읽는다
        var inner = Sta(() =>
        {
            var (tb, _) = Layout(500);
            return (System.Windows.Media.VisualTreeHelper.GetChild(tb, 0) as TextBlock)?.Text;
        });
        Assert.Equal(Long, inner);
    }
}
