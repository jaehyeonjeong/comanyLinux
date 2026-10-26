namespace ServoControlWpf.Model;

/// <summary>
/// [Model] 서보의 현재 상태(각도·펄스·출력 여부)와 사양을 보관하고, 드라이버로 출력  (Qt: model/servomodel.*)
///   상태가 바뀌면 Changed 이벤트 → ViewModel 이 화면을 갱신 (Observer 패턴)
/// </summary>
public sealed class ServoModel : IDisposable
{
    private IServoDriver _driver;

    public ServoModel(IServoDriver driver)
    {
        _driver = driver;
    }

    public event EventHandler? Changed;

    public ServoSpec Spec { get; private set; } = new();
    public double Angle { get; private set; }
    public int Pulse => Spec.AngleToPulse(Angle);
    public bool IsOutputOn { get; private set; }
    public bool IsConnected => _driver.IsRemote;
    public bool IsLinkAlive => _driver.IsLinkAlive;
    public string DriverName => _driver.Name;
    public string DriverInfo => _driver.Info;

    /// <summary>실제 장치(원격 드라이버)로 마지막으로 보낸 각도. 출력을 꺼도 서보는 물리적으로 이 자리에 머문다.</summary>
    public double? LastDrivenAngle { get; private set; }

    /// <summary>마지막 SetDriver 때 장치가 보고한 펄스폭 (null = 보고 없음). 연결 안내문에 쓴다.</summary>
    public int? DevicePulseAtConnect { get; private set; }

    /// <summary>
    /// 초기 사양 설정 (드라이버는 열린 상태로 전달).
    /// lastDrivenAngle 을 주면(이전 실행에서 저장한 값) 화면이 그 각도에서 시작한다 — 서보는 전원이 꺼져도 마지막 자리에 머물기 때문.
    /// </summary>
    public void Start(ServoSpec spec, double? lastDrivenAngle = null)
    {
        Spec = spec;
        if (lastDrivenAngle is { } a && double.IsFinite(a))
        {
            LastDrivenAngle = Spec.ClampAngle(a);
            Angle = LastDrivenAngle.Value;
        }
        OnChanged();
    }

    /// <summary>
    /// 연결/해제 시 드라이버 교체 (열린 상태로 전달).
    ///   releaseOld = true  : 이전 드라이버 출력을 끄고(펄스 0) 소멸 — 기본
    ///   releaseOld = false : 접속만 끊고 서보는 마지막 펄스를 계속 받게 둔다 ("연결 해제 시 출력 유지")
    /// 교체 뒤에는 새 장치가 지금 내보내는 펄스를 읽어 화면 상태(각도·출력)를 실제 서보에 맞춘다.
    /// </summary>
    public void SetDriver(IServoDriver driver, bool releaseOld = true)
    {
        var old = _driver;
        if (releaseOld)
            old.Release();                // 이전 드라이버 출력 정지
        old.ReleaseOnDispose = releaseOld;
        _driver = driver;
        old.Dispose();                    // 원격이면 접속 종료(+터널 종료)
        if (releaseOld)
            IsOutputOn = false;           // 출력을 유지했다면 서보는 여전히 마지막 펄스를 받고 있으므로 표시도 유지
        SyncFromDevice();
        OnChanged();
    }

    /// <summary>
    /// 장치가 보고하는 "지금 펄스폭" 에 모델을 맞춘다.
    ///   펄스 &gt; 0 : 서보가 그 펄스로 구동 중 → 각도 = PulseToAngle, 출력 켜짐 (다른 프로그램이나 이전 세션이 남긴 상태도 그대로 반영)
    ///   펄스 = 0 : 출력 꺼짐. 서보는 마지막으로 보낸 자리에 물리적으로 머물러 있으므로 그 각도로 되돌림
    ///   null    : 보고 없음(시뮬레이션 등) → 그대로 둠
    /// </summary>
    private void SyncFromDevice()
    {
        var pulse = _driver.ReadPulse();
        DevicePulseAtConnect = pulse;
        if (pulse is > 0)
        {
            Angle = Spec.ClampAngle(Spec.PulseToAngle(pulse.Value));
            IsOutputOn = true;
            LastDrivenAngle = Angle;
        }
        else if (pulse == 0)
        {
            IsOutputOn = false;
            if (LastDrivenAngle is { } last)
                Angle = Spec.ClampAngle(last);
        }
    }

    /// <summary>보정값·GPIO 변경. 실패하면 이전 사양을 유지하고 ServoException.</summary>
    public void SetSpec(ServoSpec spec)
    {
        if (!spec.IsValid())
            throw new ServoException(
                "보정값이 올바르지 않습니다.\n최소 < 중립 < 최대 (500~2500 µs), 최소각 < 0 < 최대각, GPIO 2~27 이어야 합니다.");

        if (spec.Gpio != Spec.Gpio)
        {
            _driver.Release();              // 이전 핀 출력 정지
            IsOutputOn = false;
            try
            {
                _driver.Open(spec.Gpio);
            }
            catch (ServoException)
            {
                try { _driver.Open(Spec.Gpio); } catch (ServoException) { }   // 실패하면 이전 핀으로 복구
                OnChanged();
                throw;
            }
        }

        // 새 사양으로 먼저 출력해 보고, 성공했을 때만 반영 (실패 시 이전 사양 유지)
        var newAngle = spec.ClampAngle(Angle);
        if (IsOutputOn)
        {
            try
            {
                _driver.WritePulse(spec.AngleToPulse(newAngle));
            }
            catch (ServoException)
            {
                OnChanged();
                throw;
            }
        }
        Spec = spec;
        Angle = newAngle;
        OnChanged();
    }

    /// <summary>각도로 이동 (출력 켜짐). 실패하면 상태를 바꾸지 않고 ServoException.</summary>
    public void SetAngle(double degrees)
    {
        var a = Spec.ClampAngle(degrees);
        _driver.WritePulse(Spec.AngleToPulse(a));
        Angle = a;
        IsOutputOn = true;
        if (_driver.IsRemote)
            LastDrivenAngle = a;          // 실제 서보가 실제로 간 자리만 기억 (시뮬레이션은 제외)
        OnChanged();
    }

    /// <summary>출력 끄기 (펄스 0) → 서보가 힘을 빼고 멈춤</summary>
    public void Release()
    {
        _driver.Release();
        IsOutputOn = false;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 프로그램 종료. releaseOutput = true 면 펄스 0 을 보내 서보 힘을 뺀다(기본).
    /// false 면 접속만 끊어 서보가 마지막 펄스를 계속 받으며 자리를 지킨다 → 다음 실행에서 GPW 로 그 자리를 읽어 화면을 맞춤.
    /// </summary>
    public void Dispose(bool releaseOutput)
    {
        _driver.ReleaseOnDispose = releaseOutput;
        _driver.Dispose();                 // 드라이버의 Dispose 가 (옵션에 따라) 출력 끄기까지 책임진다
        if (releaseOutput)
            IsOutputOn = false;
    }

    public void Dispose() => Dispose(releaseOutput: true);
}
