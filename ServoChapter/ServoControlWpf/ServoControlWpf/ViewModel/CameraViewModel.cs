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
///   지금은 노트북 웹캠(WinRtCameraSource)이고, 나중에 Pi 카메라는 같은 ICameraSource 규격으로 붙인다.
/// </summary>
public sealed class CameraViewModel : ObservableObject, IDisposable
{
    public static readonly CameraDevice Off = new("", "꺼짐 (카메라 사용 안 함)");

    private readonly ICameraSource _source;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _ui;
    private WriteableBitmap? _bitmap;
    private int _pending;                 // 0 = UI 가 프레임을 받을 수 있음, 1 = 처리 중
    private int _frames;
    private DateTime _fpsStamp = DateTime.UtcNow;
    private int _switchVersion;           // 빠르게 연달아 고를 때 마지막 선택만 적용

    public CameraViewModel(ICameraSource source, AppSettings settings)
    {
        _source = source;
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _source.FrameArrived += OnFrame;
        RefreshCommand = new RelayCommand(async () => await RefreshAsync(), () => !IsBusy);
        ToggleCommand = new RelayCommand(async () => await ToggleAsync(), () => !IsBusy && SelectedDevice is not null && SelectedDevice != Off);
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
                await _source.StartAsync(dev.Id);
                if (version != _switchVersion) return;       // 그 사이 다른 장치를 골랐음
                IsRunning = true;
                _frames = 0;
                _fpsStamp = DateTime.UtcNow;
                StatusText = $"{dev.Name} · 영상 수신 중";
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

    public void Dispose()
    {
        _source.FrameArrived -= OnFrame;
        _source.Dispose();
    }
}
