namespace ServoControlWpf.ViewModel;

/// <summary>위치 메모리 한 칸 (P1~P4). Angle 이 null 이면 빈 슬롯.</summary>
public sealed class PresetSlot : ObservableObject
{
    private double? _angle;

    public PresetSlot(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public double? Angle
    {
        get => _angle;
        set
        {
            if (Set(ref _angle, value))
            {
                OnPropertyChanged(nameof(HasValue));
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public bool HasValue => _angle.HasValue;

    public string Label => _angle is { } a
        ? $"P{Index + 1}\n{(a > 0 ? "+" : "")}{a:F1}°"
        : $"P{Index + 1}\n(비어 있음)";
}
