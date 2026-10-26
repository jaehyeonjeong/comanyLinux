using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ServoControlWpf.View;

/// <summary>
/// [View] 서보 모터에 올려진 카메라의 움직임을 위에서 내려다본 그림으로 보여 주는 표시 전용 화면.
///
///   - 마우스·키보드 입력을 받지 않는다 (IsHitTestVisible = false). 조작은 오른쪽 다이얼·슬라이더에서.
///   - Angle 은 "명령한 각도(목표)". 화면의 카메라는 그 목표를 향해 실제 HS-311 속도(0.19 s/60° ≈ 316 °/s)로
///     돌아가므로, 모터가 물리적으로 움직이는 모습에 가깝게 보인다.
///   - 출력이 꺼지면(펄스 0) 카메라가 회색이 되고 시야(FOV)가 사라진다 = 힘이 빠진 상태.
///   - 허용 범위(MinAngle~MaxAngle)는 연한 파란 호, 그 밖은 회색 호로 그린다.
///   - IsConnected 가 false(연결 해제)면 "연결 안 됨" 안내만 보이고, 다이얼·각도·펄스를 바꿔도 카메라는 움직이지 않는다.
///     실제 서보에 명령이 가지 않으므로 화면도 움직이지 않는 것이 맞다. 연결되고 출력이 켜지면 그때부터 목표를 따라간다.
/// </summary>
public sealed class ServoCameraView : FrameworkElement
{
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(26, 115, 232)));
    private static readonly Brush AccentLight = Freeze(new SolidColorBrush(Color.FromRgb(140, 185, 245)));
    private static readonly Brush FovBrush = Freeze(new SolidColorBrush(Color.FromArgb(46, 26, 115, 232)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(158, 158, 158)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(34, 34, 34)));
    private static readonly Brush ServoBody = Freeze(new SolidColorBrush(Color.FromRgb(38, 42, 50)));
    private static readonly Brush ServoTab = Freeze(new SolidColorBrush(Color.FromRgb(70, 76, 88)));
    private static readonly Brush HornBrush = Freeze(new SolidColorBrush(Color.FromRgb(236, 238, 242)));
    private static readonly Brush CamBodyOn = Freeze(new SolidColorBrush(Color.FromRgb(52, 58, 70)));
    private static readonly Brush CamBodyOff = Freeze(new SolidColorBrush(Color.FromRgb(176, 180, 188)));
    private static readonly Brush LensBrush = Freeze(new SolidColorBrush(Color.FromRgb(20, 24, 32)));
    private static readonly Brush LensGlass = Freeze(new SolidColorBrush(Color.FromRgb(90, 150, 230)));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(198, 40, 40)));
    private static readonly Brush DisconnectVeil = Freeze(new SolidColorBrush(Color.FromArgb(200, 245, 246, 248)));   // 연결 해제 시 그림을 덮는 반투명 막

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private readonly DispatcherTimer _timer;
    private double _shown;                 // 화면에 그려진 카메라 각도 (목표를 향해 움직임)
    private DateTime _lastTick;

    public ServoCameraView()
    {
        IsHitTestVisible = false;          // 마우스 입력을 받지 않음
        Focusable = false;
        MinWidth = 320;
        MinHeight = 240;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        Unloaded += (_, _) => _timer.Stop();
    }

    // ───────── 바인딩 가능한 속성 ─────────
    public static readonly DependencyProperty MinAngleProperty = DependencyProperty.Register(
        nameof(MinAngle), typeof(double), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(-40.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxAngleProperty = DependencyProperty.Register(
        nameof(MaxAngle), typeof(double), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAngleChanged));
    public static readonly DependencyProperty IsOutputOnProperty = DependencyProperty.Register(
        nameof(IsOutputOn), typeof(bool), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnDriveStateChanged));
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
        nameof(IsConnected), typeof(bool), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnDriveStateChanged));
    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(int), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpeedDegPerSecProperty = DependencyProperty.Register(
        nameof(SpeedDegPerSec), typeof(double), typeof(ServoCameraView), new PropertyMetadata(60.0 / 0.19));
    public static readonly DependencyProperty FieldOfViewProperty = DependencyProperty.Register(
        nameof(FieldOfView), typeof(double), typeof(ServoCameraView),
        new FrameworkPropertyMetadata(62.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double MinAngle { get => (double)GetValue(MinAngleProperty); set => SetValue(MinAngleProperty, value); }
    public double MaxAngle { get => (double)GetValue(MaxAngleProperty); set => SetValue(MaxAngleProperty, value); }
    /// <summary>명령한 각도(목표). 카메라는 이 값을 향해 서보 속도로 움직인다.</summary>
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public bool IsOutputOn { get => (bool)GetValue(IsOutputOnProperty); set => SetValue(IsOutputOnProperty, value); }
    /// <summary>Pi(또는 에뮬레이터)에 연결된 상태인지. false 면 카메라 움직임 시뮬레이션을 멈추고 안내만 보여 준다.</summary>
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }
    public int Pulse { get => (int)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }
    /// <summary>흉내 낼 서보 속도 (°/s). 기본 HS-311 @4.8 V = 60°/0.19 s</summary>
    public double SpeedDegPerSec { get => (double)GetValue(SpeedDegPerSecProperty); set => SetValue(SpeedDegPerSecProperty, value); }
    /// <summary>카메라 시야각 (°). 기본 62° (Pi 카메라 모듈 v2 수평 시야)</summary>
    public double FieldOfView { get => (double)GetValue(FieldOfViewProperty); set => SetValue(FieldOfViewProperty, value); }

    /// <summary>지금 화면에 그려진(움직이는 중인) 카메라 각도</summary>
    public double ShownAngle => _shown;
    public bool IsMoving => Math.Abs(_shown - Angle) > 0.01;
    /// <summary>회전 애니메이션 타이머가 돌고 있는지 (테스트용)</summary>
    public bool IsAnimating => _timer.IsEnabled;

    /// <summary>
    /// 카메라가 실제로 "구동"되는 조건 = 연결됨 + 출력 켜짐.
    /// 연결이 끊긴 상태에서는 어떤 값을 바꿔도 명령이 서보로 가지 않으므로 화면도 그대로 멈춰 있어야 한다.
    /// </summary>
    private bool IsDriven => IsConnected && IsOutputOn;

    private static void OnAngleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ServoCameraView)d).UpdateAnimation();

    private static void OnDriveStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ServoCameraView)d).UpdateAnimation();

    /// <summary>구동 조건을 만족하고 목표와 다르면 회전 시작, 아니면 그 자리에 멈춤.</summary>
    private void UpdateAnimation()
    {
        if (!IsDriven)
        {
            _timer.Stop();                   // 연결 해제·출력 꺼짐: 현재 위치에서 정지 (실제 서보도 힘이 빠져 그 자리에 머묾)
            return;
        }
        if (IsMoving && !_timer.IsEnabled)
        {
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsDriven) { _timer.Stop(); return; }
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        var target = Angle;
        var diff = target - _shown;
        var maxStep = SpeedDegPerSec * dt;
        _shown = Math.Abs(diff) <= maxStep ? target : _shown + Math.Sign(diff) * maxStep;
        if (!IsMoving)
        {
            _shown = target;
            _timer.Stop();
        }
        InvalidateVisual();
    }

    // ───────── 기하 ─────────
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

    private static Geometry Wedge(Point c, double r, double t1, double t2)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(c, true, true);
            ctx.LineTo(PointAt(c, r, t1), false, false);
            ctx.ArcTo(PointAt(c, r, t2), new Size(r, r), 0, Math.Abs(t2 - t1) > 180, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    private static Geometry RoundRect(Rect r, double radius)
    {
        var g = new RectangleGeometry(r, radius, radius);
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 40 || ActualHeight < 40) return;

        var w = ActualWidth;
        var h = ActualHeight;
        var c = new Point(w / 2.0, h * 0.70);                       // 서보 출력축 (카메라 회전 중심)
        var r = Math.Max(60.0, Math.Min(w / 2.0 - 60, c.Y - 56));   // 범위 호 반지름
        var min = Math.Max(-90, Math.Min(MinAngle, 0));
        var max = Math.Min(90, Math.Max(MaxAngle, 0));
        var shown = Math.Clamp(_shown, -90, 90);
        var fov = Math.Clamp(FieldOfView, 10, 150);

        var baseSize = TextElement.GetFontSize(this);
        var family = TextElement.GetFontFamily(this);
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        // 1) 바닥: 전체 반원(−90~+90) 과 허용 범위
        dc.DrawGeometry(null, new Pen(GridBrush, 1.5) { DashStyle = DashStyles.Dash }, Arc(c, r, -90, 90));
        if (max > min)
            dc.DrawGeometry(null, new Pen(AccentLight, 10) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, Arc(c, r, min, max));
        for (var t = -90; t <= 90; t += 30)
        {
            dc.DrawLine(new Pen(GridBrush, 1), PointAt(c, r + 8, t), PointAt(c, r + 18, t));
            var ft = new FormattedText($"{t}°", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(10, baseSize * 0.85), GridBrush, dip);
            var lp = PointAt(c, r + 30 + ft.Height * 0.3, t);
            dc.DrawText(ft, new Point(lp.X - ft.Width / 2, lp.Y - ft.Height / 2));
        }
        // 범위 끝 표시
        foreach (var (t, label) in new[] { (min, $"{min:0}°"), (max, $"{max:0}°") })
        {
            dc.DrawLine(new Pen(AccentBrush, 2), PointAt(c, r - 9, t), PointAt(c, r + 9, t));
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, Math.Max(10, baseSize * 0.8), AccentBrush, dip);
            var lp = PointAt(c, r - 22, t);
            dc.DrawText(ft, new Point(lp.X - ft.Width / 2, lp.Y - ft.Height / 2));
        }

        // 2) 서보 본체 (위에서 본 모습): 고정 탭 + 몸통 + 출력축. 몸통은 화면 아래쪽, 축은 c
        var bodyW = Math.Min(170.0, w * 0.34);
        var bodyH = bodyW * 0.5;
        var body = new Rect(c.X - bodyW / 2, c.Y - bodyH * 0.38, bodyW, bodyH);
        var tab = new Rect(body.X - bodyW * 0.14, body.Y + bodyH * 0.3, bodyW * 1.28, bodyH * 0.4);
        dc.DrawGeometry(ServoTab, null, RoundRect(tab, 4));
        foreach (var x in new[] { tab.X + 8, tab.Right - 8 })
            dc.DrawEllipse(Brushes.White, null, new Point(x, tab.Y + tab.Height / 2), 3, 3);
        dc.DrawGeometry(ServoBody, new Pen(Brushes.Black, 1), RoundRect(body, 6));
        var lbl = new FormattedText("HS-311", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, Math.Max(9, baseSize * 0.7), Brushes.White, dip);
        dc.DrawText(lbl, new Point(body.X + bodyW * 0.56 - lbl.Width / 2, body.Bottom - lbl.Height - 4));
        // 케이블 (노랑·빨강·검정)
        var cableX = body.Right - 6;
        foreach (var (color, dy) in new[] { (Colors.Gold, -4.0), (Colors.Red, 0.0), (Colors.Black, 4.0) })
            dc.DrawLine(new Pen(new SolidColorBrush(color), 2), new Point(cableX, body.Bottom - 10 + dy), new Point(cableX + 30, body.Bottom - 2 + dy));

        // 3) 카메라 시야 (연결 + 출력 켜짐일 때만): 회전 중심 c 에서 shown 방향으로 FOV 쐐기
        var on = IsDriven;
        if (on)
        {
            var fovR = r - 14;
            dc.DrawGeometry(FovBrush, null, Wedge(c, fovR, shown - fov / 2, shown + fov / 2));
            dc.DrawLine(new Pen(AccentBrush, 1) { DashStyle = DashStyles.Dot }, c, PointAt(c, fovR, shown));
        }

        // 4) 카메라 (회전): 혼 + 카메라 몸체 + 렌즈. 카메라의 "앞" 이 shown 방향
        dc.PushTransform(new RotateTransform(shown, c.X, c.Y));
        var camBody = on ? CamBodyOn : CamBodyOff;
        var camW = bodyW * 0.42;
        var camH = camW * 0.62;
        // 혼 (서보 암)
        dc.DrawGeometry(HornBrush, new Pen(GridBrush, 1), RoundRect(new Rect(c.X - 7, c.Y - camH * 0.9, 14, camH * 0.95), 4));
        dc.DrawEllipse(HornBrush, new Pen(GridBrush, 1), c, 11, 11);
        dc.DrawEllipse(ServoBody, null, c, 4, 4);
        // 카메라 몸체 (앞쪽이 위)
        var camRect = new Rect(c.X - camW / 2, c.Y - camH * 0.9 - camH, camW, camH);
        dc.DrawGeometry(camBody, new Pen(Brushes.Black, 1), RoundRect(camRect, 5));
        // 렌즈 (앞면에 튀어나온 원통 = 위에서 보면 사각 + 반원)
        var lensW = camW * 0.42;
        var lensRect = new Rect(c.X - lensW / 2, camRect.Y - lensW * 0.35, lensW, lensW * 0.4);
        dc.DrawGeometry(LensBrush, null, RoundRect(lensRect, 3));
        dc.DrawEllipse(on ? LensGlass : GridBrush, new Pen(LensBrush, 2), new Point(c.X, lensRect.Y + 2), lensW * 0.32, lensW * 0.2);
        // 카메라 위 표식 (앞 방향 화살표)
        dc.DrawLine(new Pen(on ? AccentLight : Brushes.White, 2), new Point(c.X, camRect.Bottom - 6), new Point(c.X, camRect.Y + 6));
        dc.Pop();

        // 제목
        var title = new FormattedText("카메라 팬 (위에서 본 모습)", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(10, baseSize * 0.85), GridBrush, dip);
        dc.DrawText(title, new Point(12, 8));

        // 5-a) 연결 해제 상태: 그림을 흐리게 덮고 안내만 표시. 각도·펄스 글자는 보여 주지 않는다 (명령이 가지 않으므로 의미 없음)
        if (!IsConnected)
        {
            dc.DrawRectangle(DisconnectVeil, null, new Rect(0, 0, w, h));
            var head = new FormattedText("연결 안 됨", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, baseSize * 1.6, GridBrush, dip);
            var body2 = new FormattedText("Pi 또는 에뮬레이터에 [연결]하면\n카메라 움직임이 여기에 표시됩니다", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(10, baseSize * 0.95), GridBrush, dip)
            { TextAlignment = TextAlignment.Center };
            var y0 = c.Y + bodyH * 0.72;
            dc.DrawText(head, new Point(w / 2 - head.Width / 2, y0));
            dc.DrawText(body2, new Point(w / 2 - body2.Width / 2, y0 + head.Height + 4));
            return;
        }

        // 5-b) 글자: 현재 각도(움직이는 값) · 목표 · 펄스 · 상태
        var big = new FormattedText($"{(shown > 0 ? "+" : "")}{shown:F1}°", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold, baseSize * 2.2, TextBrush, dip);
        dc.DrawText(big, new Point(w / 2 - big.Width / 2, c.Y + bodyH * 0.72));
        string sub;
        if (!on) sub = "출력 꺼짐 · 서보에 힘이 없음 (손으로 돌릴 수 있음)";
        else if (IsMoving) sub = $"→ 목표 {(Angle > 0 ? "+" : "")}{Angle:F1}° 로 회전 중 · {Pulse} µs";
        else sub = $"위치 유지 중 · {Pulse} µs";
        var ft2 = new FormattedText(sub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(10, baseSize * 0.9),
            !on ? GridBrush : IsMoving ? AccentBrush : TextBrush, dip);
        dc.DrawText(ft2, new Point(w / 2 - ft2.Width / 2, c.Y + bodyH * 0.72 + big.Height + 2));

        // 범위 끝에 닿아 있으면 경고 (기계적 한계 주의)
        if (on && !IsMoving && (Math.Abs(shown - MinAngle) < 0.05 || Math.Abs(shown - MaxAngle) < 0.05))
        {
            var warn = new FormattedText("끝단 — '지잉' 소리가 나면 범위를 줄이세요", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Math.Max(9, baseSize * 0.8), WarnBrush, dip);
            dc.DrawText(warn, new Point(w / 2 - warn.Width / 2, c.Y + bodyH * 0.72 + big.Height + ft2.Height + 4));
        }
    }
}
