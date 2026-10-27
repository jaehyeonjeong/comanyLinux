using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServoControlWpf.Model;

public enum ConnectionMode
{
    Direct,     // Windows → LAN → 실제 Pi 의 pigpiod(8888) 로 직접 TCP  (127.0.0.1 같은 자기 주소는 거부)
    WslTunnel,  // Windows → wsl.exe → ssh -L → Pi 의 pigpiod   (모든 통신이 WSL 을 거침)
    Emulator,   // 가짜 Pi: 앱 안(InApp) 또는 WSL 의 fake_pigpiod.py(Wsl) — 하드웨어 없이 같은 TCP 경로 검증
}

public enum EmulatorKind
{
    InApp,      // 앱 안에서 띄운 가짜 pigpiod (PigpioEmulatorServer)
    Wsl,        // WSL 에서 tools/fake_pigpiod.py 를 띄워 127.0.0.1 로 접속 (가상 네트워크를 실제로 건너감)
}

/// <summary>
/// 설정 저장 (Qt 의 QSettings ~/.config/VI/ServoControl.conf 에 해당)
///   위치: %APPDATA%\VI\ServoControl.json
/// </summary>
public sealed class AppSettings
{
    public const int PresetCount = 4;

    // ── 연결 ──
    public string Host { get; set; } = "192.168.10.2";
    public int Port { get; set; } = 8888;
    public ConnectionMode Mode { get; set; } = ConnectionMode.Direct;
    public EmulatorKind EmulatorKind { get; set; } = EmulatorKind.InApp;
    public string WslDistro { get; set; } = "";          // 비우면 WSL 기본 배포판
    public string SshAlias { get; set; } = "rpi";        // WSL ~/.ssh/config 의 Host 별명
    public int TunnelLocalPort { get; set; } = 18888;    // Windows 쪽 로컬 포트
    public string ScriptDir { get; set; } = "";          // pi_setup.sh 등이 있는 폴더 (Windows 경로 또는 /mnt/... WSL 경로)

    // ── 보정값 ──
    public int Gpio { get; set; } = 18;
    public int MinPulse { get; set; } = 1100;
    public int NeutralPulse { get; set; } = 1500;
    public int MaxPulse { get; set; } = 1900;
    public double MinAngle { get; set; } = -40;
    public double MaxAngle { get; set; } = 40;

    // ── 위치 메모리 (null = 빈 슬롯) ──
    public double?[] Presets { get; set; } = new double?[PresetCount];

    public int SweepSpeed { get; set; } = 60;   // °/s

    /// <summary>
    /// [연결 해제] 때 서보 출력을 유지할지. true 면 펄스 0 을 보내지 않아 pigpiod 가 마지막 펄스를 계속 내고,
    /// 다시 연결하면 그 펄스(GPW)를 읽어 화면을 맞춘다. 창을 닫을 때는 이 값과 무관하게 항상 출력을 끈다.
    /// </summary>
    public bool KeepOutputOnDisconnect { get; set; } = true;

    /// <summary>
    /// 실제 장치로 마지막으로 보낸 각도. 다음 실행 때 화면이 이 각도에서 시작하고,
    /// 연결 후 장치가 "출력 꺼짐(0)" 이라고 보고하면 서보가 물리적으로 머물러 있는 이 자리로 화면을 둔다.
    /// </summary>
    public double? LastDrivenAngle { get; set; }

    // ── 카메라 화면 ──
    /// <summary>선택한 카메라 장치 ID. 비우면 첫 번째 장치(보통 노트북 내장 카메라)</summary>
    public string CameraDeviceId { get; set; } = "";
    /// <summary>프로그램을 켤 때 카메라 영상을 자동으로 시작할지</summary>
    public bool CameraEnabled { get; set; } = true;

    // ── Pi 카메라 (네트워크, pi_display 의 MJPEG 스트림) ──
    /// <summary>스트림 전체 URL. 비우면 PiCameraHost(또는 Pi 주소) + PiCameraPort 로 만든다</summary>
    public string PiCameraUrl { get; set; } = "";
    /// <summary>WSL 터널 모드처럼 Pi 주소 칸이 없을 때 쓰는 Pi 호스트. 비우면 연결 설정의 Pi 주소</summary>
    public string PiCameraHost { get; set; } = "";
    public int PiCameraPort { get; set; } = 8889;
    /// <summary>각도 명령을 보낼 때 Pi 화면 프로그램에 메모("WPF +20.0°")를 UDP 로 알릴지</summary>
    public bool PiNoteEnabled { get; set; } = true;
    public int PiNotePort { get; set; } = 8890;

    // ── 녹화 영상 전송 (Pi → WSL, pi_display/pull_recordings.sh) ──
    /// <summary>WSL 안에서 녹화·사진이 모이는 폴더 (Windows 에서는 \\wsl.localhost\&lt;배포판&gt;\home\…)</summary>
    public string RecordingsDir { get; set; } = DefaultRecordingsDir;
    /// <summary>녹화를 멈추면 바로 pull_recordings.sh 로 가져올지</summary>
    public bool AutoPullRecordings { get; set; } = true;
    public const string DefaultRecordingsDir = "~/MyJob/projects/comanyLinux/ServoChapter/recordings";

    /// <summary>pi_display API 기본 주소 (스트림 URL 에서 경로를 뗀 것, 예 http://172.30.1.81:8889)</summary>
    public string ResolvePiApiBase(string? currentHost) => Services.PiCameraApi.BaseFromStreamUrl(ResolvePiCameraUrl(currentHost));

    /// <summary>실제로 접속할 Pi 카메라 URL. PiCameraUrl 이 있으면 그대로, 없으면 http://{PiCameraHost 또는 currentHost}:{PiCameraPort}/stream.mjpg</summary>
    public string ResolvePiCameraUrl(string? currentHost)
    {
        if (!string.IsNullOrWhiteSpace(PiCameraUrl)) return PiCameraUrl.Trim();
        var host = !string.IsNullOrWhiteSpace(PiCameraHost) ? PiCameraHost.Trim() : (currentHost ?? "").Trim();
        if (host.Length == 0) host = "127.0.0.1";
        return $"http://{host}:{PiCameraPort}/stream.mjpg";
    }

    /// <summary>UDP 메모를 보낼 Pi 호스트 (카메라 URL 의 호스트와 같은 규칙)</summary>
    public string ResolvePiNoteHost(string? currentHost)
    {
        if (!string.IsNullOrWhiteSpace(PiCameraUrl) && Uri.TryCreate(PiCameraUrl.Trim(), UriKind.Absolute, out var u)) return u.Host;
        var host = !string.IsNullOrWhiteSpace(PiCameraHost) ? PiCameraHost.Trim() : (currentHost ?? "").Trim();
        return host.Length == 0 ? "127.0.0.1" : host;
    }

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VI", "ServoControl.json");

    /// <summary>
    /// 실제로 저장할 경로. null 이면 Save() 가 아무것도 쓰지 않는다 (테스트용 — 테스트가 사용자의 설정 파일을 덮어쓰지 않게).
    /// </summary>
    [JsonIgnore]
    public string? StorePath { get; set; } = FilePath;

    /// <summary>에뮬레이터(가짜 pigpiod)가 펄스·각도를 기억할 상태 파일. null 이면 기억하지 않음 (테스트용)</summary>
    [JsonIgnore]
    public string? EmulatorStateFile { get; set; } = PigpioEmulator.PigpioEmulatorServer.DefaultStateFile;

    /// <summary>파일에 저장하지 않는 설정 (단위 테스트용)</summary>
    public static AppSettings InMemory() => new() { StorePath = null, EmulatorStateFile = null };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s is not null)
                {
                    if (s.Presets is null || s.Presets.Length != PresetCount)
                        s.Presets = new double?[PresetCount];
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 손상된 설정 파일은 기본값으로 시작
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (StorePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 치명적이지 않음
        }
    }

    /// <summary>저장된 보정값 (없거나 잘못되면 HS-311 공식값, GPIO 번호는 유지)</summary>
    public ServoSpec LoadSpec()
    {
        var spec = new ServoSpec(MinPulse, NeutralPulse, MaxPulse, MinAngle, MaxAngle, Gpio);
        if (!spec.IsValid())
        {
            var gpio = spec.Gpio;
            spec = ServoSpec.Hs311Official();
            if (gpio >= 2 && gpio <= 27)
                spec = spec with { Gpio = gpio };
        }
        return spec;
    }

    public void StoreSpec(ServoSpec spec)
    {
        Gpio = spec.Gpio;
        MinPulse = spec.MinPulse;
        NeutralPulse = spec.NeutralPulse;
        MaxPulse = spec.MaxPulse;
        MinAngle = spec.MinAngle;
        MaxAngle = spec.MaxAngle;
    }
}
