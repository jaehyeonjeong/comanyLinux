namespace ServoControlWpf.Model;

/// <summary>
/// [Model] 서보 사양과 "각도 ↔ 펄스폭" 변환  (Qt: model/servospec.h 와 1:1 대응)
///
/// HiTEC HS-311 제조사 사양
///   - 제어 방식: PWM, 프레임 20 ms(50 Hz), 중립 1500 µs
///   - 한쪽 400 µs → 40° 이동  ⇒  10 µs/°, 공식 동작 범위 ±40° (1100~1900 µs)
///   - 데드밴드 5 µs, 4.8~6.0 V, 0.19 s/60°(4.8 V)
/// 실제로는 600~2400 µs에서 약 ±90°까지 움직이는 개체가 많지만, 개체마다 끝단(기계적 한계)이
/// 달라서 끝까지 밀면 기어가 상할 수 있다. 그래서 기본값은 공식 범위로 둔다.
/// </summary>
public readonly record struct ServoSpec
{
    public int MinPulse { get; init; }        // µs, MinAngle 위치
    public int NeutralPulse { get; init; }    // µs, 0°
    public int MaxPulse { get; init; }        // µs, MaxAngle 위치
    public double MinAngle { get; init; }     // °
    public double MaxAngle { get; init; }     // °
    public int Gpio { get; init; }            // BCM 번호 (물리 12번 핀 = GPIO18)

    public ServoSpec()
    {
        MinPulse = 1100;
        NeutralPulse = 1500;
        MaxPulse = 1900;
        MinAngle = -40.0;
        MaxAngle = 40.0;
        Gpio = 18;
    }

    public ServoSpec(int minPulse, int neutralPulse, int maxPulse, double minAngle, double maxAngle, int gpio)
    {
        MinPulse = minPulse;
        NeutralPulse = neutralPulse;
        MaxPulse = maxPulse;
        MinAngle = minAngle;
        MaxAngle = maxAngle;
        Gpio = gpio;
    }

    /// <summary>제조사 공식 범위: 1100/1500/1900 µs, ±40°, GPIO18</summary>
    public static ServoSpec Hs311Official() => new();

    /// <summary>비공식 확장 범위: 600/1500/2400 µs, ±90°, GPIO18 (끝단 주의)</summary>
    public static ServoSpec Hs311Extended() => new(600, 1500, 2400, -90.0, 90.0, 18);

    public double ClampAngle(double a) => Math.Clamp(a, MinAngle, MaxAngle);

    /// <summary>중립 기준으로 +쪽과 −쪽을 따로 비례 계산 (양쪽 기울기가 달라도 보정 가능)</summary>
    public int AngleToPulse(double angle)
    {
        var a = ClampAngle(angle);
        if (a >= 0)
            return (int)Math.Round(NeutralPulse + a * (MaxPulse - NeutralPulse) / MaxAngle, MidpointRounding.AwayFromZero);
        return (int)Math.Round(NeutralPulse + a * (NeutralPulse - MinPulse) / -MinAngle, MidpointRounding.AwayFromZero);
    }

    public double PulseToAngle(int pulse)
    {
        if (pulse >= NeutralPulse)
            return (pulse - NeutralPulse) * MaxAngle / (double)(MaxPulse - NeutralPulse);
        return -(NeutralPulse - pulse) * -MinAngle / (double)(NeutralPulse - MinPulse);
    }

    /// <summary>pigpio 허용 범위(500~2500 µs), 최소 &lt; 중립 &lt; 최대, ±90° 이내, GPIO 2~27 인지 검사</summary>
    public bool IsValid()
    {
        return MinPulse >= 500 && MaxPulse <= 2500
               && MinPulse < NeutralPulse && NeutralPulse < MaxPulse
               && MinAngle >= -90 && MinAngle < 0 && MaxAngle > 0 && MaxAngle <= 90
               && Gpio >= 2 && Gpio <= 27;
    }

    public override string ToString()
        => $"{MinPulse}~{NeutralPulse}~{MaxPulse} µs, {MinAngle}°~{MaxAngle}°, GPIO{Gpio}";
}
