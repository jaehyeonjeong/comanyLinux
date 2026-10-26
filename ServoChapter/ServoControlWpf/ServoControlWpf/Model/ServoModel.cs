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

    /// <summary>초기 사양 설정 (드라이버는 열린 상태로 전달)</summary>
    public void Start(ServoSpec spec)
    {
        Spec = spec;
        OnChanged();
    }

    /// <summary>연결/해제 시 드라이버 교체 (열린 상태로 전달). 이전 드라이버는 출력을 끄고 소멸.</summary>
    public void SetDriver(IServoDriver driver)
    {
        var old = _driver;
        old.Release();            // 이전 드라이버 출력 정지
        _driver = driver;
        old.Dispose();            // 원격이면 접속 종료(+터널 종료)
        IsOutputOn = false;
        OnChanged();
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

    public void Dispose()
    {
        _driver.Dispose();   // 드라이버의 Dispose 가 출력 끄기(펄스 0)까지 책임진다
        IsOutputOn = false;
    }
}
