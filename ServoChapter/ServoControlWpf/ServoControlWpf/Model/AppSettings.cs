using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServoControlWpf.Model;

public enum ConnectionMode
{
    Direct,     // Windows → 이더넷 → Pi 의 pigpiod(8888) 로 직접 TCP
    WslTunnel,  // Windows → wsl.exe → ssh -L → Pi 의 pigpiod   (모든 통신이 WSL 을 거침)
    Emulator,   // Windows → 127.0.0.1 → 앱 안에서 띄운 가짜 pigpiod (하드웨어 없이 같은 TCP 경로 검증)
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

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VI", "ServoControl.json");

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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
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
