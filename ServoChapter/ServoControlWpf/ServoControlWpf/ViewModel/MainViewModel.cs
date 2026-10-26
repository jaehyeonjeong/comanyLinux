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

    public MainViewModel(ServoModel model, AppSettings settings, IDialogService dialogs)
    {
        _model = model;
        _settings = settings;
        _dialogs = dialogs;
        Wsl = new WslViewModel(settings, dialogs);

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

    // ───────── 연결 ─────────
    private string _host = "192.168.10.2";
    private int _port = 8888;
    private ConnectionMode _mode;
    private int _tunnelLocalPort;
    private bool _isBusy;

    public string Host { get => _host; set => Set(ref _host, value); }
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
        ConnectionMode.Emulator => "앱 안에서 가짜 pigpiod 를 127.0.0.1:(Pi 포트) 에 띄우고, 실제와 같은 TCP 명령으로 접속합니다",
        _ => "Windows 에서 Pi 의 pigpiod(8888) 로 바로 접속합니다",
    };

    // ───────── 에뮬레이터 상태 표시 ─────────
    private string _emulatorText = "";
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();

    /// <summary>가짜 Pi 가 실제로 받은 마지막 SERVO 명령 (TCP 로 바이트가 오갔다는 증거)</summary>
    public string EmulatorText { get => _emulatorText; private set => Set(ref _emulatorText, value); }

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
                driver = StartEmulatorDriver();
            }
            else
            {
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

            _model.SetDriver(driver);
            SaveConnectionSettings();
            ShowStatus(_mode switch
            {
                ConnectionMode.WslTunnel => "WSL 터널로 Pi에 연결했습니다. [0°]를 눌러 중앙부터 확인하세요.",
                ConnectionMode.Emulator => "가짜 Pi(에뮬레이터)에 연결했습니다. 실제 TCP 명령이 오가며, 오른쪽 위에 받은 명령이 표시됩니다.",
                _ => "Pi에 연결했습니다. [0°]를 눌러 중앙부터 확인하세요.",
            });
        }
        catch (ServoException ex)
        {
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
    /// 에뮬레이터 모드: 앱 안에 가짜 pigpiod 를 띄우고, 실제와 똑같은 RemotePigpioDriver 로 127.0.0.1 에 접속한다.
    /// (가짜 서버의 수명은 드라이버가 가지므로 연결 해제 시 함께 닫힌다)
    /// </summary>
    private IServoDriver StartEmulatorDriver()
    {
        var server = new PigpioEmulator.PigpioEmulatorServer("127.0.0.1", Port);
        try
        {
            server.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            server.Dispose();
            throw new ServoException(
                $"에뮬레이터를 127.0.0.1:{Port} 에 띄울 수 없습니다.\n({ex.Message})\n\n" +
                "다른 프로그램(또는 pigpio-emu 콘솔)이 그 포트를 쓰고 있으면 Pi 포트를 바꾸거나, " +
                "'직접 연결' 모드로 Pi 주소 127.0.0.1 에 접속하세요.", linkLost: true, inner: ex);
        }

        server.CommandReceived += ev => _ui.Post(_ =>
        {
            EmulatorText = $"가짜 Pi 수신 #{server.CommandCount}: {ev.Description}";
        }, null);
        EmulatorText = $"가짜 Pi 대기 중: {server.EndPoint}";

        return new RemotePigpioDriver("127.0.0.1", Port, server, $"에뮬레이터 127.0.0.1:{Port}");
    }

    private void OnDisconnect()
    {
        StopSweep();
        var sim = new SimulatedDriver();
        sim.Open(_model.Spec.Gpio);
        _model.SetDriver(sim);   // 이전 원격 드라이버는 출력을 끄고 접속 종료 (터널·에뮬레이터도 닫힘)
        EmulatorText = "";
        ShowStatus("연결을 끊었습니다. 서보 출력도 꺼졌습니다.");
    }

    private void RequestAngle(double angle)
    {
        StopSweep();   // 손으로 조작하면 스윕은 멈춤
        try
        {
            _model.SetAngle(angle);
        }
        catch (ServoException ex)
        {
            HandleFailure(ex);
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

    /// <summary>창을 닫을 때: 스윕 정지, 설정 저장, 출력 끄기(펄스 0), 터널 종료</summary>
    public void Shutdown()
    {
        StopSweep();
        _settings.Host = Host.Trim();
        _settings.Port = Port;
        _settings.Mode = _mode;
        _settings.TunnelLocalPort = TunnelLocalPort;
        _settings.WslDistro = Wsl.Distro ?? "";
        _settings.SshAlias = Wsl.SshAlias;
        _settings.ScriptDir = Wsl.ScriptDir;
        _settings.Save();
        Wsl.Cancel();
        _model.Dispose();
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
