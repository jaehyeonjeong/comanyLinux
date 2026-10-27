using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ServoControlWpf.ViewModel;

namespace ServoControlWpf.View;

/// <summary>
/// [View] 화면. 로직은 거의 없고 XAML 바인딩으로 MainViewModel 과 연결된다.
/// 코드 비하인드에는 "순수 화면 동작"만 둔다: 전체화면 토글, Enter 로 입력 확정, 휠로 1° 이동, 로그 자동 스크롤.
/// </summary>
public partial class MainWindow : Window
{
    public static readonly RoutedCommand ToggleFullScreenCommand = new(nameof(ToggleFullScreenCommand), typeof(MainWindow));

    private WindowState _stateBeforeFull = WindowState.Maximized;

    public MainWindow()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(ToggleFullScreenCommand, (_, _) => ToggleFullScreen()));
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFull;
        }
        else
        {
            _stateBeforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;   // Maximized 상태에서 None 으로 바꾸면 작업 표시줄이 남는 문제 회피
            WindowState = WindowState.Maximized;
        }
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>각도·펄스폭 입력칸에서 Enter → 바인딩 소스 갱신(= 이동 요청)</summary>
    private void OnInputBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not TextBox box) return;
        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        AngleDial.Focus();   // 포커스를 다이얼로 옮겨 ←/→ 키가 바로 먹게
        e.Handled = true;
    }

    /// <summary>각도 슬라이더 위에서 휠 한 칸 = 1°</summary>
    private void OnSliderWheel(object sender, MouseWheelEventArgs e)
    {
        Vm?.NudgeCommand.Execute(e.Delta > 0 ? 1.0 : -1.0);
        e.Handled = true;
    }

    /// <summary>펄스폭 슬라이더 위에서 휠 한 칸 = 10 µs</summary>
    private void OnPulseSliderWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is { } vm)
            vm.PulseInput = vm.PulseInput + (e.Delta > 0 ? 10 : -10);
        e.Handled = true;
    }

    private void OnSshBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (Vm?.Wsl.RunSshCommand.CanExecute(null) == true)
            Vm.Wsl.RunSshCommand.Execute(null);
        e.Handled = true;
    }

    private void OnLogChanged(object sender, TextChangedEventArgs e) => WslLog.ScrollToEnd();
}
