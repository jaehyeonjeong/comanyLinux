using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServoControlWpf.View;

/// <summary>
/// [View] 공간이 모자라면 글자를 자동으로 줄이는 한 줄 글 상자.
///
///   1) 글꼴을 기준 크기(<see cref="Control.FontSize"/>)에서 <see cref="MinFontSize"/> 까지 줄여 주어진 폭에 들어가게 한다.
///   2) 최소 크기로도 모자라면 "…" 으로 자르고(CharacterEllipsis),
///   3) 그때는 전체 글을 ToolTip 으로 보여 준다 (XAML 에서 ToolTip 을 따로 지정했으면 그것을 우선).
///
/// 안에 TextBlock 하나를 품고 Measure 단계에서 받은 폭(constraint, MaxWidth 로 이미 잘린 값)을 예산으로 쓴다.
/// TextBlock 을 상속하지 않은 이유: TextBlock 의 MeasureOverride 는 봉인되어 있고, 잘려 그려져도 ActualWidth 는 글 전체 폭을 보고하기 때문.
/// 글꼴·색은 Control 의 FontSize·Foreground 등이 안쪽 TextBlock 으로 상속되므로 보통 TextBlock 처럼 XAML 에 쓰면 된다.
/// </summary>
public class ShrinkTextBlock : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ShrinkTextBlock),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((ShrinkTextBlock)d)._child.Text = (string?)e.NewValue ?? ""));

    public static readonly DependencyProperty MinFontSizeProperty = DependencyProperty.Register(
        nameof(MinFontSize), typeof(double), typeof(ShrinkTextBlock),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>지금 실제로 그려지는 글꼴 크기 (줄어든 뒤 값). 테스트·UI 자동화용</summary>
    public static readonly DependencyProperty EffectiveFontSizeProperty = DependencyProperty.Register(
        nameof(EffectiveFontSize), typeof(double), typeof(ShrinkTextBlock), new PropertyMetadata(double.NaN));

    /// <summary>true 이면 지금 글이 잘려(…) 보이는 중 — 자동 ToolTip 의 근거</summary>
    public static readonly DependencyProperty IsTrimmedProperty = DependencyProperty.Register(
        nameof(IsTrimmed), typeof(bool), typeof(ShrinkTextBlock), new PropertyMetadata(false));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double MinFontSize { get => (double)GetValue(MinFontSizeProperty); set => SetValue(MinFontSizeProperty, value); }
    public double EffectiveFontSize { get => (double)GetValue(EffectiveFontSizeProperty); private set => SetValue(EffectiveFontSizeProperty, value); }
    public bool IsTrimmed { get => (bool)GetValue(IsTrimmedProperty); private set => SetValue(IsTrimmedProperty, value); }

    private readonly TextBlock _child = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private bool _toolTipIsOurs;

    static ShrinkTextBlock()
    {
        FocusableProperty.OverrideMetadata(typeof(ShrinkTextBlock), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ShrinkTextBlock), new FrameworkPropertyMetadata(false));
        // FontSize·FontFamily 등은 원래 AffectsMeasure 라 바뀌면 자동으로 다시 맞춰진다 (Control 의 속성이 안쪽 TextBlock 으로 상속됨)
    }

    public ShrinkTextBlock()
    {
        AddVisualChild(_child);
        AddLogicalChild(_child);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _child : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        var baseSize = FontSize;
        var min = Math.Min(MinFontSize, baseSize);
        var budget = constraint.Width - Padding.Left - Padding.Right;        // MaxWidth 는 WPF 가 constraint 에 이미 반영
        var hasBudget = !double.IsInfinity(budget) && budget > 0;

        var size = baseSize;
        var natural = NaturalWidth(size);
        if (hasBudget && natural > budget)
        {
            size = Math.Max(min, Math.Floor(baseSize * budget / natural * 2) / 2);   // 비례로 한 번에, 0.5 단위
            natural = NaturalWidth(size);
            while (natural > budget && size - 0.5 >= min) { size -= 0.5; natural = NaturalWidth(size); }
        }
        if (Math.Abs(_child.FontSize - size) > 0.01) _child.FontSize = size;
        if (double.IsNaN(EffectiveFontSize) || Math.Abs(EffectiveFontSize - size) > 0.01) EffectiveFontSize = size;

        var trimmed = hasBudget && natural > budget + 0.5;
        if (trimmed != IsTrimmed) IsTrimmed = trimmed;
        UpdateAutoToolTip(trimmed);

        _child.Measure(new Size(Math.Max(0, budget), Math.Max(0, constraint.Height - Padding.Top - Padding.Bottom)));
        return new Size(_child.DesiredSize.Width + Padding.Left + Padding.Right, _child.DesiredSize.Height + Padding.Top + Padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _child.Arrange(new Rect(Padding.Left, Padding.Top,
            Math.Max(0, finalSize.Width - Padding.Left - Padding.Right), Math.Max(0, finalSize.Height - Padding.Top - Padding.Bottom)));
        return finalSize;
    }

    private double NaturalWidth(double fontSize)
    {
        var text = Text ?? "";
        if (text.Length == 0) return 0;
        var dpi = PresentationSource.FromVisual(this) is null ? 1.0 : VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), fontSize, Foreground ?? Brushes.Black, dpi);
        return ft.WidthIncludingTrailingWhitespace;
    }

    private void UpdateAutoToolTip(bool trimmed)
    {
        if (ToolTip is not null && !_toolTipIsOurs) return;     // 사용자가 ToolTip 을 지정했으면 건드리지 않는다
        if (trimmed && !string.IsNullOrEmpty(Text))
        {
            if (!Equals(ToolTip, Text)) { SetCurrentValue(ToolTipProperty, Text); _toolTipIsOurs = true; }
        }
        else if (_toolTipIsOurs)
        {
            ClearValue(ToolTipProperty);
            _toolTipIsOurs = false;
        }
    }
}
