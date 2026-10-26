using System.Windows;
using System.Windows.Threading;
using ServoControlWpf.Model;
using ServoControlWpf.View;
using ServoControlWpf.ViewModel;

namespace ServoControlWpf;

/// <summary>
/// HiTEC HS-311 서보 원격 제어 프로그램 — WPF 판 (MVVM 구조)   ※ Qt 판의 main.cpp 에 해당
///
///   Windows(WPF) ──① 직접 TCP 8888──────────────────▶ Raspberry Pi 의 pigpiod ──▶ GPIO18 ──▶ 서보
///                ──② wsl.exe → ssh -L 터널 → Pi sshd ─▶ (같은 pigpiod)
///                ──③ wsl.exe → bash 스크립트 / ssh 명령 ▶ Pi 관리 (pi_setup.sh, deploy.sh, pi_info.sh)
///
///   Model/      ServoSpec(사양·변환), ServoModel(상태), IServoDriver(Remote/Simulated), PigpioClient, WslSshTunnel, AppSettings
///   ViewModel/  MainViewModel(= Qt Controller), WslViewModel
///   View/       MainWindow(XAML), ServoDial(다이얼), ServoCameraView(카메라 움직임 표시), DialogService
///   Services/   WslService, ProcessRunner
/// </summary>
public partial class App : Application
{
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var settings = AppSettings.Load();
        var spec = settings.LoadSpec();

        // ── Model: 처음에는 시뮬레이션, [연결]을 누르면 원격 드라이버로 교체 ──
        var driver = new SimulatedDriver();
        driver.Open(spec.Gpio);
        var model = new ServoModel(driver);
        model.Start(spec);

        // ── View + ViewModel ──
        var window = new MainWindow();
        _vm = new MainViewModel(model, settings, new DialogService(window));
        window.DataContext = _vm;
        window.Closed += (_, _) => ShutdownOnce();   // 창 닫기 → 펄스 0 전송, 터널 종료, 설정 저장
        MainWindow = window;
        window.WindowState = WindowState.Maximized;
        window.Show();
    }

    private void ShutdownOnce()
    {
        var vm = _vm;
        _vm = null;
        vm?.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownOnce();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("예기치 않은 오류가 발생했습니다:\n\n" + e.Exception.Message + "\n\n서보 출력을 끄고 종료합니다.",
            "서보 제어", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        ShutdownOnce();
        Shutdown(1);
    }
}
