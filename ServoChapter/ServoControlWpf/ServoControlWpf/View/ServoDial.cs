using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ServoControlWpf.View;

/// <summary>
/// [View] 원형 다이얼(노브) — Qt 의 QDial 에 해당. WPF 에는 기본 다이얼이 없어 직접 그린다.
///
///   값 범위 Minimum~Maximum 을 화면 각도 −135°~+135°(270° 스윕, 0 = 위) 에 비례로 펼친다.
///   조작: 노브를 누르거나 끌면 마우스 방향의 값으로, 휠 한 칸 = 1, ←/→ = 1, PageUp/Down = 10, Home/End = 끝.
///   Value 는 양방향 바인딩이 기본이라 ViewModel.Angle 과 그대로 묶인다.
/// </summary>
public sealed class ServoDial : FrameworkElement
{
    private const double SweepDeg = 270.0;   // 다이얼이 도는 전체 각 (−135 … +135)

    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(26, 115, 232));
    private static readonly Brush KnobLight = new SolidColorBrush(Color.FromRgb(252, 253, 255));
    private static readonly Brush KnobDark = new SolidColorBrush(Color.FromRgb(214, 220, 230));
    private static readonly Brush RingBrush = new SolidColorBrush(Color.FromRgb(201, 206, 214));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(140, 140, 140));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(11, 61, 99));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(232, 236, 242));

    static ServoDial()
    {
        foreach (var b in new[] { AccentBrush, KnobLight, KnobDark, RingBrush, GridBrush, TextBrush, TrackBrush }) b.Freeze();
    }

    public ServoDial()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MinWidth = 180;
        MinHeight = 180;
    }

    // ───────── 바인딩 가능한 속성 ─────────
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ServoDial),
        new FrameworkPropertyMetadata(-40.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ServoDial),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ServoDial),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsOutputOnProperty = DependencyProperty.Register(
        nameof(IsOutputOn), typeof(bool), typeof(ServoDial),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ServoDial), new PropertyMetadata(1.0));
    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(int), typeof(ServoDial),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>노브 안에 함께 보여 줄 펄스폭(µs). 0 이면 표시하지 않음</summary>
    public int Pulse { get => (int)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsOutputOn { get => (bool)GetValue(IsOutputOnProperty); set => SetValue(IsOutputOnProperty, value); }
    /// <summary>휠 한 칸·방향키 한 번에 움직이는 값 (기본 1°)</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    // ───────── 기하 ─────────
    private Point Center => new(ActualWidth / 2.0, ActualHeight / 2.0);
    private double OuterRadius => Math.Max(20, Math.Min(ActualWidth, ActualHeight) / 2.0 - 8);

    private double Span => Math.Max(1e-9, Maximum - Minimum);

    /// <summary>값 → 화면 각도 (0 = 위, 시계 방향 +)</summary>
    private double ValueToScreen(double v)
        => -SweepDeg / 2 + (Math.Clamp(v, Minimum, Maximum) - Minimum) / Span * SweepDeg;

    private static Point PointAt(Point c, double r, double thetaDeg)
    {
        var rad = thetaDeg * Math.PI / 180.0;
        return new Point(c.X + r * Math.Sin(rad), c.Y - r * Math.Cos(rad));
    }

    private static Geometry Arc(Point c, double r, double t1, double t2)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointAt(c, r, t1), false, false);
            ctx.ArcTo(PointAt(c, r, t2), new Size(r, r), 0, Math.Abs(t2 - t1) > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 20 || ActualHeight < 20) return;

        var c = Center;
        var rOut = OuterRadius;          // 눈금 바깥
        var rTrack = rOut - 14;          // 값 궤적(호)
        var rKnob = rOut - 30;           // 노브
        var baseSize = TextElement.GetFontSize(this);
        var family = TextElement.GetFontFamily(this);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        var screen = ValueToScreen(Value);
        var screenZero = Minimum <= 0 && Maximum >= 0 ? ValueToScreen(0) : -SweepDeg / 2;

        // 1) 바탕 궤적(회색 호) + 0° 부터 현재 값까지 채운 호(파랑)
        dc.DrawGeometry(null, new Pen(TrackBrush, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            Arc(c, rTrack, -SweepDeg / 2, SweepDeg / 2));
        if (Math.Abs(screen - screenZero) > 0.01)
        {
            var (a, b) = screen > screenZero ? (screenZero, screen) : (screen, screenZero);
            dc.DrawGeometry(null, new Pen(IsOutputOn ? AccentBrush : GridBrush, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(c, rTrack, a, b));
        }

        // 2) 눈금: 값 1 마다 작은 눈금(간격이 너무 좁으면 생략), 10 마다 큰 눈금 + 숫자
        var minorStep = Span / SweepDeg * 360 / (2 * Math.PI * rOut) < 0.12 ? 1.0 : 5.0;   // 눈금 사이가 ~4px 이상일 때만 1 단위
        var first = Math.Ceiling(Minimum);
        for (var v = first; v <= Maximum + 1e-9; v += 1)
        {
            var major = Math.Abs(v % 10) < 1e-9;
            if (!major && Math.Abs(v % minorStep) > 1e-9) continue;
            var t = ValueToScreen(v);
            dc.DrawLine(new Pen(GridBrush, major ? 2 : 1), PointAt(c, rOut - (major ? 8 : 4), t), PointAt(c, rOut, t));
            if (major)
            {
                var ft = new FormattedText($"{v:0}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face,
                    Math.Max(9, baseSize * 0.75), GridBrush, dip);
                var lp = PointAt(c, rOut + 9 + ft.Height / 2, t);
                dc.DrawText(ft, new Point(lp.X - ft.Width / 2, lp.Y - ft.Height / 2));
            }
        }
        // 양 끝 표시
        var ftMin = new FormattedText($"{Minimum:0}°", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, baseSize * 0.8, GridBrush, dip);
        var ftMax = new FormattedText($"{Maximum:0}°", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, baseSize * 0.8, GridBrush, dip);
        var pMin = PointAt(c, rTrack - 2, -SweepDeg / 2 - 10);
        var pMax = PointAt(c, rTrack - 2, SweepDeg / 2 + 10);
        dc.DrawText(ftMin, new Point(pMin.X - ftMin.Width / 2, pMin.Y + 6));
        dc.DrawText(ftMax, new Point(pMax.X - ftMax.Width / 2, pMax.Y + 6));

        // 3) 노브 (그라데이션 + 그림자 느낌의 테두리)
        var knobBrush = new RadialGradientBrush(((SolidColorBrush)KnobLight).Color, ((SolidColorBrush)KnobDark).Color)
        {
            GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75,
        };
        dc.DrawEllipse(knobBrush, new Pen(RingBrush, 2), c, rKnob, rKnob);

        // 4) 포인터: 노브 가장자리의 둥근 점 + 중심에서 나가는 선
        var pointer = IsOutputOn ? AccentBrush : GridBrush;
        dc.DrawLine(new Pen(pointer, 4) { EndLineCap = PenLineCap.Round }, PointAt(c, rKnob * 0.45, screen), PointAt(c, rKnob - 10, screen));
        dc.DrawEllipse(pointer, null, PointAt(c, rKnob - 10, screen), 6, 6);

        // 5) 가운데 값
        var big = new FormattedText($"{(Value > 0 ? "+" : "")}{Value:F1}°", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold, Math.Min(baseSize * 2.2, rKnob * 0.42), TextBrush, dip);
        dc.DrawText(big, new Point(c.X - big.Width / 2, c.Y - big.Height / 2 - rKnob * 0.05));
        var hintText = (Pulse > 0 ? $"{Pulse} µs · " : "") + (IsOutputOn ? "출력 켜짐" : "출력 꺼짐");
        var hint = new FormattedText(hintText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            face, Math.Max(9, baseSize * 0.75), IsOutputOn ? AccentBrush : GridBrush, dip);
        dc.DrawText(hint, new Point(c.X - hint.Width / 2, c.Y + big.Height / 2 - rKnob * 0.02));

        if (IsKeyboardFocused)
            dc.DrawEllipse(null, new Pen(AccentBrush, 1.5) { DashStyle = DashStyles.Dash }, c, rKnob + 4, rKnob + 4);
    }

    // ───────── 입력 ─────────
    /// <summary>마우스 위치 → 값. 중심에서 본 방향각(0 = 위, 시계 +)을 −135…+135 로 자른 뒤 값 범위에 비례.</summary>
    internal double ValueFromPoint(Point pos)
    {
        var c = Center;
        var dx = pos.X - c.X;
        var dy = c.Y - pos.Y;
        var theta = Math.Atan2(dx, dy) * 180.0 / Math.PI;           // −180…180
        theta = Math.Clamp(theta, -SweepDeg / 2, SweepDeg / 2);     // 아래쪽 빈 구간은 가까운 끝으로
        return Snap(Minimum + (theta + SweepDeg / 2) / SweepDeg * Span);
    }

    /// <summary>Step 단위로 맞추고 범위로 자름 (QDial 의 singleStep)</summary>
    internal double Snap(double v) => Math.Clamp(Math.Round(v / Step) * Step, Minimum, Maximum);

    private void SetFromMouse(Point pos) => SetValueRounded(ValueFromPoint(pos));

    private void SetValueRounded(double v)
    {
        var clamped = Snap(v);
        if (Math.Abs(clamped - Value) > 1e-9)
            Value = clamped;   // 양방향 바인딩이 ViewModel.Angle 로 전달
    }

    // ───────── UI 자동화 (접근성·자동 테스트): RangeValue 패턴으로 값을 읽고 쓸 수 있게 ─────────
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new DialPeer(this);

    private sealed class DialPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer,
        System.Windows.Automation.Provider.IRangeValueProvider
    {
        private readonly ServoDial _dial;
        public DialPeer(ServoDial dial) : base(dial) => _dial = dial;

        protected override string GetClassNameCore() => nameof(ServoDial);
        protected override string GetNameCore() => "각도 다이얼";
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.Slider;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface)
            => patternInterface == System.Windows.Automation.Peers.PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public double Value => _dial.Value;
        public bool IsReadOnly => false;
        public double Maximum => _dial.Maximum;
        public double Minimum => _dial.Minimum;
        public double LargeChange => _dial.Step * 10;
        public double SmallChange => _dial.Step;
        public void SetValue(double value) => _dial.SetValueRounded(value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        SetFromMouse(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)   // 누른 동안만 캡처되어 있으므로, 캡처 중이면 끌기 중 (펜·터치·자동화 입력도 동일하게 동작)
            SetFromMouse(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SetValueRounded(Value + (e.Delta > 0 ? Step : -Step));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: case Key.Down: SetValueRounded(Value - Step); break;
            case Key.Right: case Key.Up: SetValueRounded(Value + Step); break;
            case Key.PageDown: SetValueRounded(Value - Step * 10); break;
            case Key.PageUp: SetValueRounded(Value + Step * 10); break;
            case Key.Home: SetValueRounded(Minimum); break;
            case Key.End: SetValueRounded(Maximum); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
