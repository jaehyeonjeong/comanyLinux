using System.Collections.ObjectModel;
using System.Windows.Threading;
using ServoControlWpf.Model;

namespace ServoControlWpf.ViewModel;

/// <summary>
/// [ViewModel = Qt 의 Controller + View 상태] 화면 요청 → 모델 조작, 모델 변경 → 화면 갱신, 연결 관리, 스윕, 설정 저장
///
///   MainWindow(XAML 바인딩) ──(Command / 속성 set)──▶ MainViewModel
///   MainViewModel ──(SetDriver / SetAngle / Release / SetSpec)──▶ ServoModel
///   ServoModel ──▶ RemotePigpioDriver ──(TCP 8888 직접, 또는 127.0.0.1:18888 → WSL ssh 터널)──▶ Pi 의 pigpiod ──▶ GPIO18 펄스
///   ServoModel ──(Changed)──▶ MainViewModel ──(PropertyChanged)──▶ MainWindow
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int SweepIntervalMs = 20;   // 서보 프레임(20 ms)과 같은 주기로 갱신

    private readonly ServoModel _model;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _sweepTimer;
    private readonly DispatcherTimer _statusTimer;
    private double _sweepDir = +1;
    private bool _syncing;   // 모델→화면 갱신 중 (Qt 의 QSignalBlocker 역할: 다시 명령이 나가지 않게 막음)

    public MainViewModel(ServoModel model, AppSettings settings, IDialogService dialogs, Services.ICameraSource? cameraSource = null)
    {
        _model = model;
        _settings = settings;
        _dialogs = dialogs;
        Wsl = new WslViewModel(settings, dialogs);
        Camera = new CameraViewModel(cameraSource ?? new Services.NullCameraSource(), settings);

        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SweepIntervalMs) };
        _sweepTimer.Tick += (_, _) => OnSweepTick();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText = ""; };

        // 저장된 접속 주소·위치 메모리 불러오기
        _host = settings.Host;
        _port = settings.Port;
        _mode = settings.Mode;
        _tunnelLocalPort = settings.TunnelLocalPort;
        _sweepSpeed = Math.Clamp(settings.SweepSpeed, 5, 200);
        for (var i = 0; i < AppSettings.PresetCount; i++)
        {
            var v = settings.Presets[i];
            Presets.Add(new PresetSlot(i) { Angle = v is { } d && double.IsFinite(d) ? d : null });
        }

        // 커맨드 (Qt 의 connect(view signal → controller slot) 에 해당)
        ConnectToggleCommand = new RelayCommand(async () => await OnConnectToggleAsync(), () => !IsBusy);
        SetAngleCommand = new RelayCommand(p => { if (TryDouble(p, out var a)) RequestAngle(a); });
        NudgeCommand = new RelayCommand(p => { if (TryDouble(p, out var d)) Nudge(d); });
        CenterCommand = new RelayCommand(() => RequestAngle(0));
        GoMinCommand = new RelayCommand(() => RequestAngle(MinAngle));
        GoMaxCommand = new RelayCommand(() => RequestAngle(MaxAngle));
        ReleaseCommand = new RelayCommand(OnRelease);
        ToggleSweepCommand = new RelayCommand(() => OnSweepToggled(!IsSweeping));
        PresetRecallCommand = new RelayCommand(p => { if (p is int i) OnPresetRecall(i); });
        PresetStoreCommand = new RelayCommand(p => { if (p is int i) OnPresetStore(i); });
        ApplySpecCommand = new RelayCommand(() => OnSpecApply(CalibrationFromInputs()));
        ApplyOfficialCommand = new RelayCommand(() => OnSpecApply(ServoSpec.Hs311Official() with { Gpio = CalGpio }));
        ApplyExtendedCommand = new RelayCommand(OnApplyExtended);

        SetCalibration(_model.Spec);
        _model.Changed += (_, _) => SyncFromModel();
        SyncFromModel();
    }

    public WslViewModel Wsl { get; }

    /// <summary>왼쪽 위 "카메라 화면" (노트북 웹캠, 나중에 Pi 카메라)</summary>
    public CameraViewModel Camera { get; }

    // ───────── 연결 ─────────
    private string _host = "192.168.10.2";
    private int _port = 8888;
    private ConnectionMode _mode;
    private int _tunnelLocalPort;
    private bool _isBusy;

    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value)) OnPropertyChanged(nameof(PiCameraUrlHint)); }
    }

    // ───────── 버전 ─────────
    public string VersionText => "v" + Services.AppVersion.Short;
    public string Title => $"서보 원격 제어 · HiTEC HS-311 (WPF) v{Services.AppVersion.Short}";

    /// <summary>Pi 카메라 URL 칸을 비웠을 때 실제로 쓰이는 주소 (Pi 주소·포트에서 자동)</summary>
    public string PiCameraUrlHint => _settings.ResolvePiCameraUrl(Host);
    public int Port { get => _port; set => Set(ref _port, value); }
    public int TunnelLocalPort { get => _tunnelLocalPort; set => Set(ref _tunnelLocalPort, value); }

    /// <summary>0 = 직접 연결, 1 = WSL 터널, 2 = 에뮬레이터 (ComboBox SelectedIndex 와 바인딩)</summary>
    public int ModeIndex
    {
        get => _mode switch { ConnectionMode.WslTunnel => 1, ConnectionMode.Emulator => 2, _ => 0 };
        set
        {
            var m = value switch { 1 => ConnectionMode.WslTunnel, 2 => ConnectionMode.Emulator, _ => ConnectionMode.Direct };
            if (Set(ref _mode, m))
            {
                OnPropertyChanged(nameof(IsDirectMode));
                OnPropertyChanged(nameof(IsTunnelMode));
                OnPropertyChanged(nameof(IsEmulatorMode));
                OnPropertyChanged(nameof(ConnectHint));
            }
        }
    }

    public bool IsDirectMode => _mode == ConnectionMode.Direct;
    public bool IsTunnelMode => _mode == ConnectionMode.WslTunnel;
    public bool IsEmulatorMode => _mode == ConnectionMode.Emulator;
    public string ConnectHint => _mode switch
    {
        ConnectionMode.WslTunnel => "WSL 안의 ssh 가 Pi 의 8888 포트를 로컬로 끌어옵니다 (키 인증 필요)",
        ConnectionMode.Emulator => _settings.EmulatorKind == EmulatorKind.Wsl
            ? "WSL 에서 tools/fake_pigpiod.py 를 띄우고 127.0.0.1:(Pi 포트) 로 접속합니다 — 명령이 가상 네트워크를 실제로 건너감"
            : "앱 안에서 가짜 pigpiod 를 127.0.0.1:(Pi 포트) 에 띄우고, 실제와 같은 TCP 명령으로 접속합니다",
        _ => "LAN 의 실제 Pi(pigpiod 8888)로 바로 접속합니다. 127.0.0.1 같은 자기 주소는 에뮬레이터에서 고르세요",
    };

    /// <summary>0 = 앱 안 가짜 Pi, 1 = WSL 가짜 Pi (fake_pigpiod.py). 연결 방식이 '에뮬레이터' 일 때만 의미</summary>
    public int EmulatorKindIndex
    {
        get => _settings.EmulatorKind == EmulatorKind.Wsl ? 1 : 0;
        set
        {
            var k = value == 1 ? EmulatorKind.Wsl : EmulatorKind.InApp;
            if (_settings.EmulatorKind == k) return;
            _settings.EmulatorKind = k;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectHint));
            OnPropertyChanged(nameof(EmulatorKindHint));
        }
    }
    public string EmulatorKindHint => _settings.EmulatorKind == EmulatorKind.Wsl
        ? "WSL 의 python3 fake_pigpiod.py 를 띄움 (이미 떠 있으면 그것 사용)"
        : "가짜 Pi 를 127.0.0.1 에 띄움";

    /// <summary>직접 연결은 실제 Pi(LAN 주소)에만. 자기 자신(127.x, localhost, 0.0.0.0)은 에뮬레이터로 안내</summary>
    public static bool IsLoopbackHost(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return true;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(h, out var ip))
            return System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any);
        return false;
    }

    // ───────── 에뮬레이터 상태 표시 ─────────
    private string _emulatorText = "";
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();

    /// <summary>
    /// 앱 안의 가짜 pigpiod. 드라이버가 아니라 ViewModel 이 수명을 가져서 [연결 해제] 뒤에도 살아 있고,
    /// 실제 pigpiod 처럼 마지막 펄스·위치를 기억한다. 다시 [연결]하면 GPW 로 그 상태를 읽어 화면을 맞춘다.
    /// </summary>
    private PigpioEmulator.PigpioEmulatorServer? _emuServer;

    /// <summary>가짜 Pi 가 실제로 받은 마지막 명령, 또는 해제 뒤 기억하고 있는 상태 (TCP 로 바이트가 오갔다는 증거)</summary>
    public string EmulatorText { get => _emulatorText; private set => Set(ref _emulatorText, value); }

    /// <summary>
    /// [연결 해제]·프로그램 종료 때 서보 출력을 유지할지. 켜져 있으면 펄스 0 을 보내지 않아 서보가 위치를 지키고,
    /// 다시 연결하면(다음 실행이라도) 장치의 현재 펄스(GPW)를 읽어 다이얼·슬라이더·카메라가 그 위치로 맞춰진다.
    /// </summary>
    public bool KeepOutputOnDisconnect
    {
        get => _settings.KeepOutputOnDisconnect;
        set
        {
            if (_settings.KeepOutputOnDisconnect == value) return;
            _settings.KeepOutputOnDisconnect = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditConnection));
                ConnectToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsConnected => _model.IsConnected;
    public bool CanEditConnection => !IsConnected && !IsBusy;
    public string ConnectButtonText => IsBusy ? "연결 중…" : IsConnected ? "연결 해제" : "연결";
    public string DriverName => _model.DriverName;
    public string ConnInfoText => IsConnected ? _model.DriverInfo : "Pi에 연결하면 실제 서보가 움직입니다";

    // ───────── 서보 상태 ─────────
    private double _angle;
    public double Angle
    {
        get => _angle;
        set
        {
            if (_syncing) { Set(ref _angle, value); return; }
            if (Math.Abs(value - _angle) < 1e-9) return;
            RequestAngle(value);            // 슬라이더·입력칸에서 온 요청
            OnPropertyChanged();            // 실패했으면 이전 값으로 되돌려 보이게
        }
    }

    public double MinAngle => _model.Spec.MinAngle;
    public double MaxAngle => _model.Spec.MaxAngle;
    public int Pulse => _model.Pulse;
    public bool IsOutputOn => _model.IsOutputOn;
    public string PulseText => $"펄스폭 {(IsOutputOn ? Pulse.ToString() : "—")} µs";
    public string OutputText => IsOutputOn ? $"출력 켜짐 · {Pulse} µs 유지 중" : "출력 꺼짐 · 손으로 돌릴 수 있음";
    public string AngleBigText => $"{(Angle > 0 ? "+" : "")}{Angle:F1}°";

    // ───────── 펄스폭으로 조작 (각도 ↔ 펄스폭 양방향) ─────────
    public int MinPulse => _model.Spec.MinPulse;
    public int MaxPulse => _model.Spec.MaxPulse;

    /// <summary>
    /// 펄스폭 입력칸·슬라이더와 바인딩. 값을 넣으면 사양의 PulseToAngle 로 각도를 구해 그 각도로 이동(출력)하고,
    /// 모델이 바뀌면(각도 조작·스윕·보정) 다시 AngleToPulse 로 계산된 값이 표시된다.
    /// </summary>
    public double PulseInput
    {
        get => _model.Pulse;
        set
        {
            if (_syncing) return;
            var spec = _model.Spec;
            var pulse = (int)Math.Round(Math.Clamp(value, spec.MinPulse, spec.MaxPulse));
            if (pulse == _model.Pulse) { OnPropertyChanged(); return; }
            RequestAngle(spec.PulseToAngle(pulse));
            OnPropertyChanged();   // 실패했으면 이전 값으로 되돌려 보이게
        }
    }

    // ───────── 상태 바 ─────────
    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    // ───────── 스윕 ─────────
    private bool _isSweeping;
    private int _sweepSpeed;
    public bool IsSweeping
    {
        get => _isSweeping;
        private set { if (Set(ref _isSweeping, value)) OnPropertyChanged(nameof(SweepButtonText)); }
    }
    public string SweepButtonText => IsSweeping ? "스윕 정지" : "스윕 시작";
    public int SweepSpeed
    {
        get => _sweepSpeed;
        set
        {
            if (Set(ref _sweepSpeed, value))
            {
                OnPropertyChanged(nameof(SweepSpeedText));
                _settings.SweepSpeed = value;
            }
        }
    }
    public string SweepSpeedText => $"{SweepSpeed} °/s";

    // ───────── 위치 메모리 ─────────
    public ObservableCollection<PresetSlot> Presets { get; } = new();

    // ───────── 보정 입력칸 ─────────
    private int _calGpio, _calMinPulse, _calNeutral, _calMaxPulse;
    private double _calMinAngle, _calMaxAngle;
    public int CalGpio { get => _calGpio; set => Set(ref _calGpio, value); }
    public int CalMinPulse { get => _calMinPulse; set => Set(ref _calMinPulse, value); }
    public int CalNeutralPulse { get => _calNeutral; set => Set(ref _calNeutral, value); }
    public int CalMaxPulse { get => _calMaxPulse; set => Set(ref _calMaxPulse, value); }
    public double CalMinAngle { get => _calMinAngle; set => Set(ref _calMinAngle, value); }
    public double CalMaxAngle { get => _calMaxAngle; set => Set(ref _calMaxAngle, value); }

    // ───────── 커맨드 ─────────
    public RelayCommand ConnectToggleCommand { get; }
    public RelayCommand SetAngleCommand { get; }
    public RelayCommand NudgeCommand { get; }
    public RelayCommand CenterCommand { get; }
    public RelayCommand GoMinCommand { get; }
    public RelayCommand GoMaxCommand { get; }
    public RelayCommand ReleaseCommand { get; }
    public RelayCommand ToggleSweepCommand { get; }
    public RelayCommand PresetRecallCommand { get; }
    public RelayCommand PresetStoreCommand { get; }
    public RelayCommand ApplySpecCommand { get; }
    public RelayCommand ApplyOfficialCommand { get; }
    public RelayCommand ApplyExtendedCommand { get; }

    // ═════════ 요청 처리 (Qt ServoController 의 슬롯들) ═════════

    private async Task OnConnectToggleAsync()
    {
        if (IsConnected) { OnDisconnect(); return; }

        StopSweep();
        IsBusy = true;
        try
        {
            IServoDriver driver;
            if (_mode == ConnectionMode.WslTunnel)
            {
                var progress = new Progress<string>(s => ShowStatus(s));
                var tunnel = await WslSshTunnel.OpenAsync(
                    Wsl.Distro, Wsl.SshAlias, TunnelLocalPort, Port, progress: progress);
                driver = new RemotePigpioDriver(tunnel.LocalHost, tunnel.LocalPort, tunnel, tunnel.Describe() + " → pigpiod");
            }
            else if (_mode == ConnectionMode.Emulator)
            {
                driver = _settings.EmulatorKind == EmulatorKind.Wsl
                    ? await StartWslFakePiDriverAsync(new Progress<string>(s => ShowStatus(s)))
                    : StartEmulatorDriver();
            }
            else
            {
                if (IsLoopbackHost(Host))
                    throw new ServoException(
                        $"직접 연결은 실제 Pi(LAN 주소, 예 192.168.10.2 또는 172.30.1.81)에만 씁니다.\n" +
                        $"'{Host.Trim()}' 은(는) 이 PC 자신의 주소입니다.\n\n" +
                        "가짜 Pi 로 연습하려면 연결 방식을 '에뮬레이터' 로 바꾸고 종류에서 '앱 안 가짜 Pi' 또는 'WSL 가짜 Pi' 를 고르세요.", linkLost: true);
                driver = new RemotePigpioDriver(Host.Trim(), Port);
            }

            try
            {
                await Task.Run(() => driver.Open(_model.Spec.Gpio));   // 1.5초 타임아웃 동안 화면이 멈추지 않게
            }
            catch
            {
                driver.Dispose();
                throw;
            }

            _model.SetDriver(driver);     // 교체 직후 장치의 현재 펄스(GPW)를 읽어 각도·출력 표시를 실제 서보에 맞춤
            SaveConnectionSettings();
            var where = _mode switch
            {
                ConnectionMode.WslTunnel => "WSL 터널로 Pi에",
                ConnectionMode.Emulator => "가짜 Pi(에뮬레이터)에",
                _ => "Pi에",
            };
            if (_model.DevicePulseAtConnect is > 0)
                ShowStatus($"{where} 연결했습니다. 서보가 이미 {_model.Angle:+0.0;-0.0}° ({_model.Pulse} µs)로 구동 중이어서 화면을 그 위치에 맞췄습니다.");
            else if (_model.DevicePulseAtConnect == 0 && _model.LastDrivenAngle is { } last)
                ShowStatus($"{where} 연결했습니다. 서보 출력은 꺼져 있고 마지막 위치 {last:+0.0;-0.0}° 로 화면을 맞췄습니다.");
            else
                ShowStatus(_mode == ConnectionMode.Emulator
                    ? "가짜 Pi(에뮬레이터)에 연결했습니다. 실제 TCP 명령이 오가며, 오른쪽 위에 받은 명령이 표시됩니다."
                    : $"{where} 연결했습니다. [0°]를 눌러 중앙부터 확인하세요.");
            if (_mode == ConnectionMode.Emulator && _settings.EmulatorKind == EmulatorKind.InApp && EmulatorFallbackPort is { } fp)
                ShowStatus($"에뮬레이터 연결됨 — 포트 {Port} 은 다른 프로그램(예: WSL 의 fake_pigpiod.py)이 쓰고 있어 임시 포트 {fp} 에 띄웠습니다. " +
                           "그 서버에 붙고 싶으면 에뮬레이터 종류를 'WSL 가짜 Pi' 로 바꾸세요.");
        }
        catch (ServoException ex)
        {
            // 오류 창은 모달이므로 그 전에 '연결 중…' 상태를 풀어 둔다 (창이 떠 있는 동안 버튼이 잠겨 보이지 않게)
            IsBusy = false;
            RaiseConnectionProps();
            ShowStatus("연결 실패");
            _dialogs.ShowError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("연결 취소");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 예상 못 한 오류라도 앱이 죽지 않고 원인을 보여 주게 함 (연결 전이므로 서보 상태는 바뀌지 않음)
            IsBusy = false;
            RaiseConnectionProps();
            ShowStatus("연결 실패");
            _dialogs.ShowError("연결 중 예상하지 못한 오류가 났습니다:\n\n" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            IsBusy = false;
            RaiseConnectionProps();
        }
    }

    /// <summary>
    /// 에뮬레이터 모드: 앱 안에 가짜 pigpiod 를 띄우고(처음 한 번), 실제와 똑같은 RemotePigpioDriver 로 127.0.0.1 에 접속한다.
    /// 가짜 서버는 [연결 해제] 뒤에도 그대로 두어 실제 pigpiod 처럼 마지막 펄스·위치를 기억하게 한다 (창을 닫을 때 종료).
    /// </summary>
    private int _emuRequestedPort = -1;   // 서버를 띄울 때 사용자가 적어 둔 Pi 포트 (바뀌면 새로 띄움)

    /// <summary>에뮬레이터가 실제로 듣는 포트가 Pi 포트와 다를 때(점유로 임시 포트를 쓸 때) 그 포트. 같으면 null.</summary>
    public int? EmulatorFallbackPort { get; private set; }

    private IServoDriver StartEmulatorDriver()
    {
        if (_emuServer is not null && _emuRequestedPort != Port)
        {
            _emuServer.Dispose();         // 포트를 바꿨으면 새로 띄움 (상태 파일 덕분에 기억은 유지)
            _emuServer = null;
        }
        if (_emuServer is null)
        {
            // 상태 파일 덕분에 프로그램을 껐다 켜도 "같은 가짜 서보" 가 마지막 펄스·각도를 가진 채 다시 뜬다
            var server = new PigpioEmulator.PigpioEmulatorServer("127.0.0.1", Port, _settings.EmulatorStateFile);
            EmulatorFallbackPort = null;
            try
            {
                server.Start();
            }
            catch (System.Net.Sockets.SocketException)
            {
                // 포트가 이미 쓰이는 중 (흔한 경우: WSL 에서 fake_pigpiod.py 나 pigpio-emu 를 띄워 두었을 때 — WSL2 가 그 포트를 Windows 의
                // 127.0.0.1 로도 열어 준다). 에뮬레이터는 포트가 무엇이든 상관없으므로 빈 포트(0)로 다시 띄우고 사용자에게 알린다.
                server.Dispose();
                server = new PigpioEmulator.PigpioEmulatorServer("127.0.0.1", 0, _settings.EmulatorStateFile);
                try
                {
                    server.Start();
                }
                catch (System.Net.Sockets.SocketException ex2)
                {
                    server.Dispose();
                    throw new ServoException(
                        $"에뮬레이터를 127.0.0.1 에 띄울 수 없습니다.\n({ex2.Message})", linkLost: true, inner: ex2);
                }
                EmulatorFallbackPort = server.EndPoint.Port;
            }
            server.CommandReceived += ev => _ui.Post(_ =>
            {
                if (_model.IsConnected)
                    EmulatorText = (EmulatorFallbackPort is { } p ? $"가짜 Pi(임시 포트 {p}) 수신" : "가짜 Pi 수신")
                                   + $" #{server.CommandCount}: {ev.Description}";
            }, null);
            _emuServer = server;
            _emuRequestedPort = Port;
            EmulatorText = EmulatorFallbackPort is { } fp
                ? $"가짜 Pi 대기 중: 127.0.0.1:{fp} (포트 {Port} 은 다른 프로그램이 사용 중이라 임시 포트)"
                : $"가짜 Pi 대기 중: {server.EndPoint}";
        }

        var port = _emuServer.EndPoint.Port;
        // tunnel 자리에 서버를 넘기지 않는다 → 드라이버가 사라져도(연결 해제) 서버는 계속 살아 기억을 유지
        return new RemotePigpioDriver("127.0.0.1", port, null, $"에뮬레이터 127.0.0.1:{port}");
    }

    // ───────── 에뮬레이터 종류 2: WSL 의 fake_pigpiod.py ─────────
    private WslFakePi? _wslFakePi;

    private async Task<IServoDriver> StartWslFakePiDriverAsync(IProgress<string> progress)
    {
        if (_wslFakePi is not null && _wslFakePi.Port != Port)
        {
            _wslFakePi.Dispose();         // 포트를 바꿨으면 새로
            _wslFakePi = null;
        }
        if (_wslFakePi is null)
        {
            var fp = await WslFakePi.OpenAsync(Wsl.Distro, Port, progress);
            fp.LineReceived += line => _ui.Post(_ =>
            {
                if (_model.IsConnected || !fp.OwnsProcess)
                    EmulatorText = "WSL 가짜 Pi: " + line;
            }, null);
            _wslFakePi = fp;
        }
        EmulatorText = _wslFakePi.OwnsProcess
            ? $"WSL 가짜 Pi(fake_pigpiod.py) 127.0.0.1:{Port} 띄움 — 받은 명령이 여기에 표시됩니다"
            : $"이미 떠 있는 WSL 가짜 Pi 127.0.0.1:{Port} 사용 (명령 로그는 그쪽 터미널에)";
        // 서버(프로세스)는 ViewModel 이 가지므로 드라이버에 넘기지 않는다 → 해제해도 살아 있고, 상태 파일로 기억
        return new RemotePigpioDriver("127.0.0.1", Port, null, _wslFakePi.Describe());
    }

    /// <summary>WSL 가짜 Pi 가 기억 중인 펄스 (GPW 를 잠깐 읽어 봄)</summary>
    private string WslFakePiMemoryText()
    {
        try
        {
            using var c = new PigpioClient("127.0.0.1", Port, 800);
            c.Connect(800);
            var p = c.Command(PigpioClient.CmdGetServoPulsewidth, (uint)_model.Spec.Gpio, 0);
            return p > 0
                ? $"WSL 가짜 Pi 기억 중: GPIO{_model.Spec.Gpio} {p} µs ({_model.Spec.PulseToAngle(p):+0.0;-0.0}°) 유지 — 다시 [연결]하면 이 위치로 화면을 맞춥니다"
                : $"WSL 가짜 Pi 기억 중: GPIO{_model.Spec.Gpio} 출력 꺼짐";
        }
        catch (ServoException)
        {
            return "WSL 가짜 Pi: 지금은 응답 없음 (꺼졌거나 아직 서보 명령을 받은 적 없음)";
        }
    }

    /// <summary>해제 뒤 가짜 Pi 가 기억하고 있는 상태 (실제 pigpiod 가 접속과 무관하게 펄스를 유지하는 것과 같음)</summary>
    private string EmulatorMemoryText()
    {
        if (_settings.EmulatorKind == EmulatorKind.Wsl) return WslFakePiMemoryText();
        if (_emuServer is null) return "";
        var gpio = _model.Spec.Gpio;
        var pulse = _emuServer.GetPulse(gpio);
        var angle = _emuServer.GetSimulatedAngle(gpio);
        return pulse > 0
            ? $"가짜 Pi 기억 중: GPIO{gpio} {pulse} µs ({angle:+0.0;-0.0}°) 유지 — 다시 [연결]하면 이 위치로 화면을 맞춥니다"
            : $"가짜 Pi 기억 중: GPIO{gpio} 출력 꺼짐, 서보는 {angle:+0.0;-0.0}° 에 멈춰 있음";
    }

    private void OnDisconnect()
    {
        StopSweep();
        // 출력 유지 옵션: 서보가 실제로 구동 중일 때만 의미가 있다 (이미 꺼져 있으면 그냥 끊음)
        var keep = KeepOutputOnDisconnect && _model.IsConnected && _model.IsOutputOn;
        var keptAngle = _model.Angle;
        var keptPulse = _model.Pulse;
        var sim = new SimulatedDriver();
        sim.Open(_model.Spec.Gpio);
        _model.SetDriver(sim, releaseOld: !keep);   // keep 이면 펄스 0 을 보내지 않고 접속만 끊음 (터널은 닫힘, 에뮬레이터 서버는 유지)
        _settings.LastDrivenAngle = _model.LastDrivenAngle;   // 다음 실행 때 이 각도에서 시작
        _settings.Save();
        EmulatorText = _mode == ConnectionMode.Emulator ? EmulatorMemoryText() : "";
        ShowStatus(keep
            ? $"연결을 끊었습니다. 서보는 마지막 위치 {keptAngle:+0.0;-0.0}° ({keptPulse} µs)를 유지합니다. 다시 연결하면 그 위치로 화면을 맞춥니다."
            : "연결을 끊었습니다. 서보 출력도 꺼졌습니다.");
    }

    private void RequestAngle(double angle)
    {
        StopSweep();   // 손으로 조작하면 스윕은 멈춤
        try
        {
            _model.SetAngle(angle);
            SendPiNote($"WPF {_model.Angle:+0.0;-0.0}° {_model.Pulse}us");
        }
        catch (ServoException ex)
        {
            HandleFailure(ex);
        }
    }

    private System.Net.Sockets.UdpClient? _noteUdp;

    /// <summary>
    /// (선택) Pi 화면 프로그램(pi_display)에 "누가 움직였는지" 메모를 UDP 로 보낸다. 실패해도 무시 — 서보 제어와 무관한 보조 기능.
    /// 실제 Pi 에 연결된 상태(원격 드라이버)에서만, 스윕 중에는 보내지 않는다(초당 50개가 되므로).
    /// </summary>
    private void SendPiNote(string text)
    {
        if (!_settings.PiNoteEnabled || !_model.IsConnected || IsSweeping || _mode == ConnectionMode.Emulator) return;
        try
        {
            _noteUdp ??= new System.Net.Sockets.UdpClient();
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            _noteUdp.Send(bytes, bytes.Length, _settings.ResolvePiNoteHost(Host), _settings.PiNotePort);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ArgumentException)
        {
            // 보조 기능이므로 조용히 무시
        }
    }

    private void Nudge(double delta) => RequestAngle(Math.Clamp(Angle + delta, MinAngle, MaxAngle));

    /// <summary>통신이 끊긴 오류면 먼저 연결을 정리한 뒤 알림 (pigpio 가 거부한 오류는 연결 유지)</summary>
    private void HandleFailure(ServoException ex)
    {
        if (_model.IsConnected && (ex.LinkLost || !_model.IsLinkAlive))
        {
            OnDisconnect();
            _dialogs.ShowError(ex.Message + "\n\n연결을 해제했습니다. 다시 [연결]을 눌러 주세요.");
            return;
        }
        _dialogs.ShowError(ex.Message);
    }

    private void OnRelease()
    {
        StopSweep();
        _model.Release();
        ShowStatus("출력을 껐습니다. 서보에 힘이 빠집니다.");
    }

    private void OnSweepToggled(bool on)
    {
        if (!on) { StopSweep(); return; }
        _sweepDir = _model.Angle >= _model.Spec.MaxAngle ? -1 : +1;
        _sweepTimer.Start();
        IsSweeping = true;
    }

    private void OnSweepTick()
    {
        var spec = _model.Spec;
        var next = _model.Angle + _sweepDir * SweepSpeed * SweepIntervalMs / 1000.0;
        if (next >= spec.MaxAngle) { next = spec.MaxAngle; _sweepDir = -1; }
        else if (next <= spec.MinAngle) { next = spec.MinAngle; _sweepDir = +1; }

        try
        {
            _model.SetAngle(next);
        }
        catch (ServoException ex)
        {
            StopSweep();
            HandleFailure(ex);
        }
    }

    private void StopSweep()
    {
        if (_sweepTimer.IsEnabled)
            _sweepTimer.Stop();
        IsSweeping = false;
    }

    private void OnPresetRecall(int slot)
    {
        if (slot < 0 || slot >= Presets.Count || Presets[slot].Angle is not { } a)
            return;
        RequestAngle(a);
    }

    private void OnPresetStore(int slot)
    {
        if (slot < 0 || slot >= Presets.Count)
            return;
        Presets[slot].Angle = _model.Angle;
        for (var i = 0; i < Presets.Count; i++)
            _settings.Presets[i] = Presets[i].Angle;
        _settings.Save();
        ShowStatus($"P{slot + 1}에 {_model.Angle:F1}° 저장");
    }

    private void OnSpecApply(ServoSpec spec)
    {
        StopSweep();
        try
        {
            _model.SetSpec(spec);
        }
        catch (ServoException ex)
        {
            SetCalibration(_model.Spec);   // 입력칸을 실제 적용값으로 되돌림
            HandleFailure(ex);
            return;
        }
        _settings.StoreSpec(spec);
        _settings.Save();
        SetCalibration(spec);
        ShowStatus($"보정값 적용: {spec}");
    }

    private void OnApplyExtended()
    {
        var ok = _dialogs.Confirm("확장값 적용",
            "600~2400 µs(±90°)는 제조사 공식 범위(±40°)를 넘습니다.\n" +
            "끝단에서 '지잉' 소리가 나면 기어에 무리가 가니 바로 범위를 줄이세요.\n적용할까요?");
        if (!ok) return;
        OnSpecApply(ServoSpec.Hs311Extended() with { Gpio = CalGpio });
    }

    // ═════════ 화면 갱신 ═════════

    /// <summary>모델 → 화면 (Qt syncView/showState). _syncing 으로 되돌아오는 명령을 막음.</summary>
    private void SyncFromModel()
    {
        _syncing = true;
        try
        {
            Angle = _model.Angle;
            OnPropertyChanged(nameof(MinAngle));
            OnPropertyChanged(nameof(MaxAngle));
            OnPropertyChanged(nameof(MinPulse));
            OnPropertyChanged(nameof(MaxPulse));
            OnPropertyChanged(nameof(Pulse));
            OnPropertyChanged(nameof(PulseInput));
            OnPropertyChanged(nameof(IsOutputOn));
            OnPropertyChanged(nameof(PulseText));
            OnPropertyChanged(nameof(OutputText));
            OnPropertyChanged(nameof(AngleBigText));
            RaiseConnectionProps();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RaiseConnectionProps()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanEditConnection));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(DriverName));
        OnPropertyChanged(nameof(ConnInfoText));
    }

    private void SetCalibration(ServoSpec spec)
    {
        CalGpio = spec.Gpio;
        CalMinPulse = spec.MinPulse;
        CalNeutralPulse = spec.NeutralPulse;
        CalMaxPulse = spec.MaxPulse;
        CalMinAngle = spec.MinAngle;
        CalMaxAngle = spec.MaxAngle;
    }

    private ServoSpec CalibrationFromInputs()
        => new(CalMinPulse, CalNeutralPulse, CalMaxPulse, CalMinAngle, CalMaxAngle, CalGpio);

    private void SaveConnectionSettings()
    {
        _settings.Host = Host.Trim();
        _settings.Port = Port;
        _settings.Mode = _mode;
        _settings.TunnelLocalPort = TunnelLocalPort;
        _settings.WslDistro = Wsl.Distro ?? "";
        _settings.SshAlias = Wsl.SshAlias;
        _settings.Save();
    }

    public void ShowStatus(string text)
    {
        StatusText = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>
    /// 창을 닫을 때: 스윕 정지, 설정 저장(마지막 각도 포함), 터널 종료.
    /// 출력은 "출력 유지" 옵션이 꺼져 있을 때만 끈다(펄스 0). 켜져 있으면 서보가 자리를 지키고, 다음 실행에서 GPW 로 읽어 화면을 맞춘다.
    /// </summary>
    public void Shutdown()
    {
        StopSweep();
        var keep = KeepOutputOnDisconnect && _model.IsConnected && _model.IsOutputOn;
        _settings.LastDrivenAngle = _model.LastDrivenAngle;
        _settings.Host = Host.Trim();
        _settings.Port = Port;
        _settings.Mode = _mode;
        _settings.TunnelLocalPort = TunnelLocalPort;
        _settings.WslDistro = Wsl.Distro ?? "";
        _settings.SshAlias = Wsl.SshAlias;
        _settings.ScriptDir = Wsl.ScriptDir;
        _settings.Save();
        Wsl.Cancel();
        Camera.Dispose();          // 웹캠·네트워크 카메라 닫기
        _noteUdp?.Dispose();
        _model.Dispose(releaseOutput: !keep);   // 가짜 서버가 아직 살아 있어야 펄스 0 명령을 받을 수 있으므로 서버보다 먼저
        _emuServer?.Dispose();                  // 상태 파일에 마지막 펄스·각도 저장
        _emuServer = null;
        _wslFakePi?.Dispose();                  // 우리가 띄운 WSL fake_pigpiod.py 종료 (상태 파일로 기억은 유지)
        _wslFakePi = null;
    }

    private static bool TryDouble(object? p, out double v)
    {
        switch (p)
        {
            case double d: v = d; return true;
            case int i: v = i; return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r): v = r; return true;
            default: v = 0; return false;
        }
    }
}
