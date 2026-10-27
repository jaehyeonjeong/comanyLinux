using System.Globalization;

namespace PigpioEmulator;

/// <summary>
/// 가짜 Pi 가 "펄스 → 각도" 를 계산할 때 쓰는 보정값. 실제 pigpiod 는 펄스폭만 알고 각도를 모른다 —
/// 각도는 보는 쪽(WPF·에뮬레이터·Pi 화면)이 각자 계산하므로, 셋이 <b>같은 보정값</b>을 써야 같은 숫자가 나온다.
/// WPF 의 <c>ServoSpec</c> 과 같은 공식(중립 기준 양쪽 따로 비례), 문자열 형식은 pi_display 의 <c>--calib</c> 와 같다:
/// "최소펄스,중립펄스,최대펄스,최소각,최대각" (예 "600,1500,2400,-80,80").
///
/// WPF 가 보정값을 가짜 Pi 에 전달하는 길: pigpio 의 사용자 정의 명령 CF1(87) 두 번 —
///   p1=1: p2 = 최소펄스 | 중립펄스&lt;&lt;16, p3 = 최대펄스
///   p1=2: p2 = 최소각×10 (int32), p3 = 최대각×10 (int32)
/// 실제 pigpiod 는 CF1 을 custom.cext 가 없으면 그냥 0 으로 답하므로 보내도 해가 없지만, WPF 는 에뮬레이터 모드에서만 보낸다.
/// </summary>
public sealed record ServoCalibration(int MinPulse, int NeutralPulse, int MaxPulse, double MinAngle, double MaxAngle)
{
    public const uint CmdCustom1 = 87;      // pigpio CF1
    public const uint SubPulses = 1;
    public const uint SubAngles = 2;

    /// <summary>HS-311 제조사 공식값 (WPF 기본값과 같음)</summary>
    public static ServoCalibration Default => new(1100, 1500, 1900, -40, 40);

    public bool IsValid()
        => MinPulse >= 500 && MaxPulse <= 2500 && MinPulse < NeutralPulse && NeutralPulse < MaxPulse
           && MinAngle >= -90 && MinAngle < 0 && MaxAngle > 0 && MaxAngle <= 90;

    public double PulseToAngle(int pulse)
    {
        if (pulse >= NeutralPulse)
            return (pulse - NeutralPulse) * MaxAngle / (double)(MaxPulse - NeutralPulse);
        return -(NeutralPulse - pulse) * -MinAngle / (double)(NeutralPulse - MinPulse);
    }

    /// <summary>"600,1500,2400,-80,80"</summary>
    public string ToCalibString()
        => string.Join(",", MinPulse, NeutralPulse, MaxPulse,
            MinAngle.ToString("0.##", CultureInfo.InvariantCulture), MaxAngle.ToString("0.##", CultureInfo.InvariantCulture));

    public static bool TryParse(string? text, out ServoCalibration calib)
    {
        calib = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(',');
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[0].Trim(), out var mn) || !int.TryParse(parts[1].Trim(), out var ne) || !int.TryParse(parts[2].Trim(), out var mx)
            || !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a0)
            || !double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a1))
            return false;
        var c = new ServoCalibration(mn, ne, mx, a0, a1);
        if (!c.IsValid()) return false;
        calib = c;
        return true;
    }

    public override string ToString() => $"{MinPulse}~{NeutralPulse}~{MaxPulse} µs, {MinAngle:0.#}°~{MaxAngle:0.#}°";

    // ── CF1 패킹 ──
    public (uint p2, uint p3) PackPulses() => ((uint)MinPulse | ((uint)NeutralPulse << 16), (uint)MaxPulse);
    public (uint p2, uint p3) PackAngles() => (unchecked((uint)(int)Math.Round(MinAngle * 10)), unchecked((uint)(int)Math.Round(MaxAngle * 10)));

    /// <summary>CF1 p1=1 을 받아 펄스 세 값을 바꾼 보정값. 잘못되면 null.</summary>
    public ServoCalibration? WithPackedPulses(uint p2, uint p3)
    {
        var c = this with { MinPulse = (int)(p2 & 0xFFFF), NeutralPulse = (int)(p2 >> 16), MaxPulse = (int)(p3 & 0xFFFF) };
        return c.MinPulse >= 500 && c.MaxPulse <= 2500 && c.MinPulse < c.NeutralPulse && c.NeutralPulse < c.MaxPulse ? c : null;
    }

    /// <summary>CF1 p1=2 를 받아 각도 두 값을 바꾼 보정값. 잘못되면 null.</summary>
    public ServoCalibration? WithPackedAngles(uint p2, uint p3)
    {
        var c = this with { MinAngle = unchecked((int)p2) / 10.0, MaxAngle = unchecked((int)p3) / 10.0 };
        return c.MinAngle >= -90 && c.MinAngle < 0 && c.MaxAngle > 0 && c.MaxAngle <= 90 ? c : null;
    }
}
