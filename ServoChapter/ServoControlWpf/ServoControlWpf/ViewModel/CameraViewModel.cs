using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ServoControlWpf.Model;
using ServoControlWpf.Services;

namespace ServoControlWpf.ViewModel;

/// <summary>
/// 왼쪽 위 "카메라 화면" 패널. 카메라 장치 목록(콤보박스), 선택·시작·정지, 영상 표시.
///
///   기본 동작: 프로그램을 켜면 장치 목록을 읽고, 저장된 장치(없으면 첫 번째 = 보통 노트북 내장 카메라)를 자동으로 켠다.
///   영상은 ICameraSource.FrameArrived(백그라운드 스레드) → UI 스레드로 넘겨 WriteableBitmap 에 쓴다.
///   화면이 바쁘면 프레임을 건너뛴다 (_pending) — 밀린 프레임을 쌓아 두지 않음.
///
///   "Pi 카메라 (네트워크)" 를 골라 영상이 들어오면 같은 서버(pi_display, 8889)의 API 로
///   [● 녹화]/[■ 정지] · [📷 사진] · 카메라 설정(녹화 모드·반전·밝기·대비·채도·화이트밸런스) · 인체/사물 인식을 조작하고,
///   1 초마다 status.json 을 읽어 녹화 시간·용량·인식 결과를 보여 준다. 설정 슬라이더는 300 ms 디바운스 뒤 한 번에 보낸다.
///   녹화가 끝나거나 사진이 저장되면 PiFileSaved 를 올리고, MainViewModel 이 WSL 의 pull_recordings.sh 로 가져온다.
/// </summary>
public sealed class CameraViewModel : ObservableObject, IDisposable
{
    public static readonly CameraDevice Off = new("", "꺼짐 (카메라 사용 안 함)");
    public static readonly string[] DefaultRecordModes = { "1080p30", "720p60", "480p90" };
    public static readonly string[] DefaultAwbModes = { "auto", "daylight", "cloudy", "tungsten", "fluorescent", "indoor" };

    private readonly ICameraSource _source;
    private readonly AppSettings _settings;
    private readonly PiCameraApi? _api;
    private readonly SynchronizationContext _ui;
    private WriteableBitmap? _bitmap;
    private int _pending;                 // 0 = UI 가 프레임을 받을 수 있음, 1 = 처리 중
    private int _frames;
    private DateTime _fpsStamp = DateTime.UtcNow;
    private int _switchVersion;           // 빠르게 연달아 고를 때 마지막 선택만 적용

    public CameraViewModel(ICameraSource source, AppSettings settings, PiCameraApi? api = null)
    {
        _source = source;
        _settings = settings;
        _api = api;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _source.FrameArrived += OnFrame;
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsBusy);
        ToggleCommand = new RelayCommand(async () => await ToggleAsync(), () => !IsBusy && SelectedDevice is not null && SelectedDevice != Off);
        RecordCommand = new RelayCommand(async () => await ToggleRecordAsync(), () => CanControlPi && !IsPiBusy);
        SnapshotCommand = new RelayCommand(async () => await SnapshotAsync(), () => CanControlPi && !IsPiBusy);
    }

    public ObservableCollection<CameraDevice> Devices { get; } = new();

    private CameraDevice? _selected;
    /// <summary>콤보박스 선택. 바꾸면 그 장치로 바로 전환(설정에 저장). "꺼짐" 이면 정지.</summary>
    public CameraDevice? SelectedDevice
    {
        get => _selected;
        set
        {
            if (value is null) return;       // 창이 닫히며 콤보박스가 선택을 비우는 경우 등 — 설정을 바꾸지 않는다
            if (!Set(ref _selected, value)) return;
            _settings.CameraDeviceId = value is null || value == Off ? "" : value.Id;
            _settings.CameraEnabled = value is not null && value != Off;
            _settings.Save();
            ToggleCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsNetworkSelected));
            OnPropertyChanged(nameof(IsPiPanelVisible));
            RaisePiState();
            _ = SwitchAsync();
        }
    }

    /// <summary>선택한 장치가 네트워크(Pi MJPEG) 카메라인지 — URL 입력 줄을 보일지 결정</summary>
    public bool IsNetworkSelected => CompositeCameraSource.IsNetworkId(_selected?.Id);

    /// <summary>
    /// Pi 카메라 스트림 URL (설정 저장). 비우면 Pi 주소에서 자동으로 만든다 (MainViewModel.PiCameraUrlHint 참고).
    /// 네트워크 카메라가 돌고 있는 중에 바꾸면 새 URL 로 다시 붙는다.
    /// </summary>
    public string PiCameraUrl
    {
        get => _settings.PiCameraUrl;
        set
        {
            var v = (value ?? "").Trim();
            if (_settings.PiCameraUrl == v) return;
            _settings.PiCameraUrl = v;
            _settings.Save();
            OnPropertyChanged();
            if (IsNetworkSelected && IsRunning)
                _ = SwitchAsync();
        }
    }

    private ImageSource? _frame;
    /// <summary>화면에 바인딩되는 영상 (WriteableBitmap, 같은 객체를 계속 덮어씀)</summary>
    public ImageSource? Frame { get => _frame; private set => Set(ref _frame, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(OverlayText));
                RaisePiState();
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                ToggleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _statusText = "카메라 준비 중…";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _overlay = "";
    /// <summary>영상이 없을 때 검은 화면 가운데에 보이는 안내 (영상이 나오면 빈 문자열)</summary>
    public string OverlayText
    {
        get => IsRunning ? "" : _overlay;
        private set { _overlay = value; OnPropertyChanged(); }
    }

    private string _fpsText = "";
    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }
    public string ToggleText => IsRunning ? "정지" : "시작";
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleCommand { get; }

    /// <summary>프로그램 시작 시: 장치 목록 → 저장된/첫 번째 장치 선택 → (설정이 켜져 있으면) 자동 시작</summary>
    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<CameraDevice> list;
            try
            {
                list = await _source.ListAsync();
            }
            catch (CameraException ex)
            {
                list = Array.Empty<CameraDevice>();
                StatusText = ex.Message;
            }

            Devices.Clear();
            foreach (var d in list) Devices.Add(d);
            Devices.Add(Off);

            // 저장된 장치가 있으면 그것, 없으면 첫 번째(노트북 카메라), 장치가 없으면 꺼짐
            CameraDevice pick;
            if (!_settings.CameraEnabled) pick = Off;
            else pick = list.FirstOrDefault(d => d.Id == _settings.CameraDeviceId) ?? list.FirstOrDefault() ?? Off;

            if (list.Count == 0)
                OverlayText = "연결된 카메라가 없습니다\n(나중에 Pi 카메라를 같은 자리에 표시합니다)";

            // Set 을 통하지 않고 직접 넣어 설정 저장 없이 전환
            if (!Equals(_selected, pick))
            {
                _selected = pick;
                OnPropertyChanged(nameof(SelectedDevice));
                OnPropertyChanged(nameof(IsNetworkSelected));
                OnPropertyChanged(nameof(IsPiPanelVisible));
                ToggleCommand.RaiseCanExecuteChanged();
            }
            await SwitchAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
            StatusText = "카메라 정지됨";
            OverlayText = "카메라 정지됨 — [시작] 을 누르면 다시 켭니다";
            _settings.CameraEnabled = false;
            _settings.Save();
        }
        else
        {
            _settings.CameraEnabled = true;
            _settings.Save();
            await SwitchAsync();
        }
    }

    /// <summary>현재 선택 장치로 전환. 꺼짐이면 정지.</summary>
    private async Task SwitchAsync()
    {
        var version = ++_switchVersion;
        var dev = _selected;
        IsBusy = true;
        try
        {
            if (dev is null || dev == Off || !_settings.CameraEnabled)
            {
                await StopAsync();
                if (Devices.Count <= 1)
                {
                    StatusText = "연결된 카메라가 없습니다";      // 목록에 '꺼짐' 뿐 (안내문은 RefreshAsync 가 이미 넣음)
                }
                else if (dev == Off)
                {
                    StatusText = "카메라 꺼짐";
                    OverlayText = "카메라 꺼짐 — 목록에서 장치를 고르면 켜집니다";
                }
                return;
            }

            StatusText = $"{dev.Name} 여는 중…";
            OverlayText = StatusText;
            try
            {
                StopPiPolling();
                await _source.StartAsync(dev.Id);
                if (version != _switchVersion) return;       // 그 사이 다른 장치를 골랐음
                IsRunning = true;
                _frames = 0;
                _fpsStamp = DateTime.UtcNow;
                StatusText = $"{dev.Name} · 영상 수신 중";
                if (CanControlPi) StartPiPolling();
            }
            catch (CameraException ex)
            {
                if (version != _switchVersion) return;
                IsRunning = false;
                Frame = null;
                _bitmap = null;
                StatusText = "카메라 오류: " + ex.Message.Split('\n')[0];
                OverlayText = ex.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        StopPiPolling();
        await _source.StopAsync();
        IsRunning = false;
        FpsText = "";
    }

    // ───────── 프레임 처리 ─────────
    private void OnFrame(CameraFrame f)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;   // UI 가 아직 이전 프레임을 그리는 중이면 건너뜀
        _ui.Post(_ =>
        {
            try { Render(f); }
            finally { Interlocked.Exchange(ref _pending, 0); }
        }, null);
    }

    private void Render(CameraFrame f)
    {
        if (!IsRunning) return;
        if (_bitmap is null || _bitmap.PixelWidth != f.Width || _bitmap.PixelHeight != f.Height)
        {
            _bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgr32, null);
            FrameWidth = f.Width;
            FrameHeight = f.Height;
            Frame = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);

        _frames++;
        var elapsed = (DateTime.UtcNow - _fpsStamp).TotalSeconds;
        if (elapsed >= 1.0)
        {
            FpsText = $"{f.Width}×{f.Height} · {_frames / elapsed:0} fps";
            _frames = 0;
            _fpsStamp = DateTime.UtcNow;
        }
    }

    // ═════════ Pi 카메라 조작 (녹화 · 사진 · 설정 · 인식) ═════════

    /// <summary>Pi API 가 연결되어 있고(앱 조립), 네트워크 카메라를 골랐으면 패널을 보인다 (영상이 아직 안 와도)</summary>
    public bool IsPiPanelVisible => _api is not null && IsNetworkSelected;
    /// <summary>실제로 명령을 보낼 수 있는 상태: 네트워크 카메라 영상 수신 중</summary>
    public bool CanControlPi => _api is not null && IsNetworkSelected && IsRunning;

    private bool _isPiBusy;
    /// <summary>녹화 시작/정지·사진 요청이 Pi 에 가 있는 동안 (버튼 잠금)</summary>
    public bool IsPiBusy
    {
        get => _isPiBusy;
        private set { if (Set(ref _isPiBusy, value)) RaisePiState(); }
    }

    private void RaisePiState()
    {
        OnPropertyChanged(nameof(CanControlPi));
        OnPropertyChanged(nameof(IsPiPanelVisible));
        RecordCommand.RaiseCanExecuteChanged();
        SnapshotCommand.RaiseCanExecuteChanged();
    }

    public RelayCommand RecordCommand { get; }
    public RelayCommand SnapshotCommand { get; }

    /// <summary>녹화 파일이 닫혔거나 사진이 저장되었을 때 (파일 이름). MainViewModel 이 WSL 로 가져오기를 시작한다.</summary>
    public event Action<string>? PiFileSaved;

    // ── 녹화 상태 ──
    private bool _isRecording;
    private double _recSeconds;
    private long _recBytes;
    private string? _recFile;

    public bool IsRecording
    {
        get => _isRecording;
        private set { if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(RecordText)); }
    }

    /// <summary>버튼 글: "● 녹화" ↔ "■ 정지 00:01:23 · 12 MB"</summary>
    public string RecordText => IsRecording ? $"■ 정지 {Hms(_recSeconds)} · {Mb(_recBytes)}" : "● 녹화";

    private string _recordInfoText = "";
    /// <summary>마지막 녹화·사진·전송 결과 한 줄</summary>
    public string RecordInfoText { get => _recordInfoText; private set => Set(ref _recordInfoText, value); }

    /// <summary>MainViewModel 이 전송(pull) 결과를 적어 준다</summary>
    public void SetTransferResult(string text) => RecordInfoText = text;

    /// <summary>마지막 녹화 명령(시작/정지 응답) 시각. 그 전에 보낸 상태 폴링 응답은 낡은 것이므로 녹화 상태에 반영하지 않는다.</summary>
    private long _recordCommandTick;

    private async Task ToggleRecordAsync()
    {
        if (_api is null || !CanControlPi) return;
        IsPiBusy = true;
        try
        {
            if (IsRecording)
            {
                var r = await _api.RecordStopAsync();
                _recordCommandTick = Environment.TickCount64;
                OnRecordingFinished(r);
            }
            else
            {
                var st = await _api.RecordStartAsync();
                _recordCommandTick = Environment.TickCount64;
                _lastFinishedName = null;
                ApplyRecord(st);
                RecordInfoText = $"녹화 중: {st.File}  ({KindLabel(st.Kind)}, Pi 폴더 {st.Dir})";
            }
        }
        catch (CameraException ex)
        {
            RecordInfoText = "녹화 오류: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            IsPiBusy = false;
        }
    }

    private string? _lastFinishedName;

    private void OnRecordingFinished(PiRecordResult r)
    {
        // 정지 응답과 상태 폴링이 같은 끝남을 둘 다 알릴 수 있으므로 한 번만 처리 (가져오기를 두 번 돌리지 않게)
        if (r.Name.Length > 0 && r.Name == _lastFinishedName) return;
        _lastFinishedName = r.Name;
        IsRecording = false;
        _recSeconds = 0; _recBytes = 0; _recFile = null;
        OnPropertyChanged(nameof(RecordText));
        RecordInfoText = $"녹화 저장: {r.Name} · {Mb(r.Bytes)} · {Hms(r.Seconds)}" + (r.Reason.Length > 0 ? $" ({r.Reason})" : "");
        PiFileSaved?.Invoke(r.Name);
    }

    private async Task SnapshotAsync()
    {
        if (_api is null || !CanControlPi) return;
        IsPiBusy = true;
        try
        {
            var r = await _api.SnapshotSaveAsync();
            RecordInfoText = $"사진 저장: {r.Name} · {r.Width}×{r.Height} · {Mb(r.Bytes)}" + (r.Note.Length > 0 ? $" ({r.Note})" : "");
            PiFileSaved?.Invoke(r.Name);
        }
        catch (CameraException ex)
        {
            RecordInfoText = "사진 오류: " + ex.Message.Split('\n')[0];
        }
        finally
        {
            IsPiBusy = false;
        }
    }

    /// <summary>창을 닫을 때: 녹화 중이면 파일이 닫히도록 정지를 보낸다 (최대 timeout). 전송은 WSL 감시 스크립트에 맡긴다.</summary>
    public async Task StopRecordingIfAnyAsync(TimeSpan timeout)
    {
        if (_api is null || !IsRecording) return;
        using var cts = new CancellationTokenSource(timeout);
        try { await _api.RecordStopAsync(cts.Token); }
        catch (Exception ex) when (ex is CameraException or OperationCanceledException) { }
    }

    // ── 인식 ──
    private bool _isDetectOn;
    private bool _detectAvailable;
    /// <summary>체크박스. 바꾸면 Pi 에 /detect/on|off 를 보낸다.</summary>
    public bool IsDetectOn
    {
        get => _isDetectOn;
        set
        {
            if (!Set(ref _isDetectOn, value)) return;
            if (_applyingRemote || _api is null) return;
            _ = SetDetectAsync(value);
        }
    }

    private string _detectText = "";
    /// <summary>"인식: 사람 1 · 의자 2 (MobileNet-SSD 4.1 fps)" / "인식 불가 (OpenCV 없음)"</summary>
    public string DetectText { get => _detectText; private set => Set(ref _detectText, value); }

    private async Task SetDetectAsync(bool on)
    {
        if (_api is null) return;
        try
        {
            var st = await _api.DetectSetAsync(on);
            _ui.Post(_ => ApplyDetect(st), null);
        }
        catch (CameraException ex)
        {
            _ui.Post(_ =>
            {
                _applyingRemote = true;
                try { IsDetectOn = false; } finally { _applyingRemote = false; }
                DetectText = "인식 오류: " + ex.Message.Split('\n')[0];
            }, null);
        }
    }

    private void ApplyDetect(PiDetectStatus st)
    {
        _applyingRemote = true;
        try { IsDetectOn = st.Enabled; }
        finally { _applyingRemote = false; }
        _detectAvailable = st.Available;
        if (!st.Available) DetectText = "인식 불가 (OpenCV 없음) — Pi 에 python3-opencv 를 설치하세요";
        else if (!st.Enabled) DetectText = $"인식 꺼짐 ({st.Model})";
        else DetectText = $"인식: {st.Summary} ({st.Model} {st.Fps:0.0} fps, {st.InferMs} ms)";
    }

    // ── 카메라 설정 ──
    public string[] RecordModes { get; private set; } = DefaultRecordModes;
    public string[] AwbModes { get; private set; } = DefaultAwbModes;

    private string _recordMode = "1080p30";
    private bool _hflip, _vflip;
    private double _brightness, _contrast = 1.0, _saturation = 1.0;
    private string _awb = "auto";
    private bool _applyingRemote;          // 서버 값을 화면에 넣는 중 (다시 서버로 보내지 않게)
    private bool _settingsLoaded;

    public string RecordMode { get => _recordMode; set { if (Set(ref _recordMode, value ?? "1080p30")) Queue("record_mode", _recordMode); } }
    public bool Hflip { get => _hflip; set { if (Set(ref _hflip, value)) Queue("hflip", value); } }
    public bool Vflip { get => _vflip; set { if (Set(ref _vflip, value)) Queue("vflip", value); } }
    public double Brightness { get => _brightness; set { if (Set(ref _brightness, Math.Round(value, 2))) Queue("brightness", _brightness); } }
    public double Contrast { get => _contrast; set { if (Set(ref _contrast, Math.Round(value, 2))) Queue("contrast", _contrast); } }
    public double Saturation { get => _saturation; set { if (Set(ref _saturation, Math.Round(value, 2))) Queue("saturation", _saturation); } }
    public string Awb { get => _awb; set { if (Set(ref _awb, value ?? "auto")) Queue("awb", _awb); } }

    private string _piSettingsInfo = "";
    /// <summary>"녹화 1920×1080 @30 · H.264(하드웨어) · 사진 2592×1944 · main …"</summary>
    public string PiSettingsInfo { get => _piSettingsInfo; private set => Set(ref _piSettingsInfo, value); }

    /// <summary>설정 변경을 모아서 보내기 전 기다리는 시간 (테스트에서 줄임)</summary>
    public int SettingsDebounceMs { get; set; } = 300;
    /// <summary>status.json 읽기 간격</summary>
    public int PollIntervalMs { get; set; } = 1000;

    private readonly Dictionary<string, object> _pendingSettings = new();
    private CancellationTokenSource? _debounce;
    /// <summary>마지막으로 보낸 설정 요청 (테스트가 기다릴 수 있게)</summary>
    public Task? LastSettingsFlush { get; private set; }

    private void Queue(string key, object value)
    {
        if (_applyingRemote || _api is null) return;
        _pendingSettings[key] = value;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        LastSettingsFlush = FlushLaterAsync(cts.Token);
    }

    private async Task FlushLaterAsync(CancellationToken ct)
    {
        try { await Task.Delay(SettingsDebounceMs, ct); }
        catch (OperationCanceledException) { return; }
        Dictionary<string, object> batch;
        lock (_pendingSettings)
        {
            if (_pendingSettings.Count == 0) return;
            batch = new Dictionary<string, object>(_pendingSettings);
            _pendingSettings.Clear();
        }
        if (_api is null) return;
        try
        {
            var r = await _api.SetSettingsAsync(batch);
            _ui.Post(_ =>
            {
                LoadSettings(r.Settings);
                if (r.Restarted) RecordInfoText = $"카메라를 {r.Settings.RecordMode} 모드로 다시 열었습니다 (반전 h={r.Settings.Hflip} v={r.Settings.Vflip})";
            }, null);
        }
        catch (CameraException ex)
        {
            _ui.Post(_ => RecordInfoText = "설정 오류: " + ex.Message.Split('\n')[0], null);
        }
    }

    private void LoadSettings(PiCameraSettings s)
    {
        _applyingRemote = true;
        try
        {
            RecordMode = s.RecordMode; Hflip = s.Hflip; Vflip = s.Vflip;
            Brightness = s.Brightness; Contrast = s.Contrast; Saturation = s.Saturation; Awb = s.Awb;
        }
        finally { _applyingRemote = false; }
        _settingsLoaded = true;
    }

    private void ApplySettingsInfo(PiCameraSettingsInfo info)
    {
        if (info.RecordModes.Length > 0 && !info.RecordModes.SequenceEqual(RecordModes)) { RecordModes = info.RecordModes; OnPropertyChanged(nameof(RecordModes)); }
        if (info.AwbModes.Length > 0 && !info.AwbModes.SequenceEqual(AwbModes)) { AwbModes = info.AwbModes; OnPropertyChanged(nameof(AwbModes)); }
        if (!_settingsLoaded) LoadSettings(info.Settings);
        if (info.RecordCapable == "h264")
        {
            var size = info.RecordSize.Length == 2 ? $"{info.RecordSize[0]}×{info.RecordSize[1]} @{info.RecordFps}" : info.Settings.RecordMode;
            var still = info.StillSize.Length == 2 ? $"{info.StillSize[0]}×{info.StillSize[1]}" : "-";
            PiSettingsInfo = $"녹화 {size} · {KindLabel(info.RecordCapable)} · 사진 {still}" + (info.BackendDetail.Length > 0 ? $" · {info.BackendDetail}" : "");
        }
        else
        {
            // picamera2 가 아닌 백엔드(가짜 카메라·libcamera-vid·USB): 녹화·사진 모두 미리보기 크기, 녹화 모드·반전·밝기는 저장만 됨
            PiSettingsInfo = $"이 카메라 백엔드는 미리보기 크기로 녹화·사진 ({KindLabel(info.RecordCapable)}) · 녹화 모드 {info.Settings.RecordMode} 는 picamera2 에서만 적용"
                             + (info.BackendDetail.Length > 0 ? $" · {info.BackendDetail}" : "");
        }
    }

    // ── 상태 폴링 ──
    private CancellationTokenSource? _pollCts;

    private void StartPiPolling()
    {
        if (_api is null) return;
        StopPiPolling();
        _settingsLoaded = false;
        var cts = _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(cts.Token), CancellationToken.None);
    }

    private void StopPiPolling()
    {
        var cts = _pollCts;
        _pollCts = null;
        cts?.Cancel();
        if (IsRecording) { IsRecording = false; _recSeconds = 0; _recBytes = 0; OnPropertyChanged(nameof(RecordText)); }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        if (_api is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var issued = Environment.TickCount64;
                var st = await _api.StatusAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                _ui.Post(_ => { if (!ct.IsCancellationRequested) ApplyStatus(st, issued); }, null);
            }
            catch (CameraException ex)
            {
                if (ct.IsCancellationRequested) return;
                _ui.Post(_ => { if (!ct.IsCancellationRequested) DetectText = "Pi 상태 읽기 실패: " + ex.Message.Split('\n')[0]; }, null);
            }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// status.json 한 장을 화면에 반영 (UI 스레드). 테스트에서 직접 호출할 수 있다.
    /// issuedTick 은 그 요청을 보낸 시각 — 마지막 녹화 명령보다 먼저 보낸(낡은) 응답은 녹화 상태를 덮어쓰지 않는다.
    /// </summary>
    public void ApplyStatus(PiStatus st, long issuedTick = long.MaxValue)
    {
        if (st.Servo is not null) ApplyServoCalib(st.Servo);
        if (st.Record is not null && issuedTick >= _recordCommandTick) ApplyRecord(st.Record);
        if (st.Detect is not null) ApplyDetect(st.Detect);
        if (st.Settings is not null) ApplySettingsInfo(st.Settings);
    }

    private void ApplyRecord(PiRecordStatus r)
    {
        var was = IsRecording;
        _recSeconds = r.Seconds; _recBytes = r.Bytes;
        if (r.Recording)
        {
            _recFile = r.File;
            IsRecording = true;
            OnPropertyChanged(nameof(RecordText));
        }
        else if (was)
        {
            // 우리가 멈춘 게 아닌데 끝났음 (시간 제한·Pi 쪽 정지) → 같은 마무리
            if (r.Last is not null && r.Last.Name == _recFile) OnRecordingFinished(r.Last);
            else { IsRecording = false; _recFile = null; OnPropertyChanged(nameof(RecordText)); }
        }
    }

    // ── 서보 보정값 맞추기 ──
    /// <summary>WPF 의 현재 보정값 문자열("600,1500,2400,-80,80")을 돌려주는 함수 (MainViewModel 이 꽂음)</summary>
    public Func<string>? CalibProvider { get; set; }

    private string _calibText = "";
    /// <summary>"Pi 화면 보정값 600~1500~2400 µs, -80°~80° (WPF 와 같음)" / 전달 실패 안내</summary>
    public string CalibText { get => _calibText; private set => Set(ref _calibText, value); }

    private string? _piCalibSeen;
    private long? _lastCalibPushTick;

    private void ApplyServoCalib(PiServoStatus s)
    {
        if (string.IsNullOrEmpty(s.Calib)) return;
        _piCalibSeen = s.Calib;
        var want = CalibProvider?.Invoke();
        if (want is null) { CalibText = $"Pi 화면 보정값 {s.Calib}"; return; }
        if (want == s.Calib)
        {
            CalibText = $"Pi 화면 보정값 {s.Calib} (WPF 와 같음 — 양쪽 각도가 일치)";
            return;
        }
        // 다르면 WPF 값으로 맞춘다 (5 초에 한 번만 시도)
        if (_lastCalibPushTick is null || Environment.TickCount64 - _lastCalibPushTick.Value > 5000)
            _ = PushCalibrationAsync();
    }

    /// <summary>Pi 화면 프로그램에 WPF 보정값을 보낸다 (네트워크 카메라로 Pi 에 붙어 있을 때만). 보정값을 바꿀 때 MainViewModel 이 부른다.</summary>
    public async Task PushCalibrationAsync()
    {
        if (_api is null || CalibProvider is null || _pollCts is null) return;
        var want = CalibProvider();
        _lastCalibPushTick = Environment.TickCount64;
        try
        {
            var r = await _api.SetServoCalibAsync(want);
            _ui.Post(_ =>
            {
                _piCalibSeen = r.Calib;
                CalibText = $"Pi 화면 보정값을 WPF 와 같게 맞췄습니다: {r.Text}";
            }, null);
        }
        catch (CameraException ex)
        {
            _ui.Post(_ => CalibText = "Pi 화면 보정값 전달 실패 (pi_display 가 옛 버전?): " + ex.Message.Split('\n')[0], null);
        }
    }

    // ── 도우미 ──
    internal static string Hms(double seconds)
    {
        var s = (int)Math.Max(0, seconds);
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }

    internal static string Mb(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
         : bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024:0.0} MB"
         : $"{bytes / 1024.0:0} KB";

    internal static string KindLabel(string kind) => kind switch
    {
        "h264" => "H.264 하드웨어 인코더 → mp4",
        "mjpeg-avi" => "MJPEG → AVI (미리보기 화질)",
        _ => kind,
    };

    public void Dispose()
    {
        StopPiPolling();
        _debounce?.Cancel();
        _source.FrameArrived -= OnFrame;
        _source.Dispose();
        _api?.Dispose();
    }
}
