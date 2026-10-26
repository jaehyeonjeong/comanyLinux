using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServoControlWpf.ViewModel;

/// <summary>INotifyPropertyChanged 를 짧게 쓰기 위한 밑바탕 (외부 패키지 없이 직접 구현)</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>값이 바뀌었을 때만 저장하고 알림. 바뀌었으면 true.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
