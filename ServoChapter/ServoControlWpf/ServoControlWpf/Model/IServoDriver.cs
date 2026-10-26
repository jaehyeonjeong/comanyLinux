namespace ServoControlWpf.Model;

/// <summary>
/// [Model] 서보에 펄스를 내보내는 하드웨어 계층의 공통 인터페이스 (Strategy 패턴)
///   - RemotePigpioDriver : TCP로 Pi의 pigpiod에 명령 전송 (실제 서보) — 직접 연결 / WSL 터널 / 에뮬레이터 모두 사용
///   - SimulatedDriver    : 연결 전·테스트용, 화면만 동작
/// 실패는 <see cref="ServoException"/> 으로 알린다.
/// </summary>
public interface IServoDriver : IDisposable
{
    string Name { get; }                 // 상태 표시용 이름
    string Info { get; }                 // 연결된 장치 정보 (Pi 리비전 등)
    bool IsRemote { get; }
    bool IsLinkAlive { get; }            // 통신 경로가 살아 있는지

    /// <summary>
    /// Dispose 할 때 출력을 끌지(기본 true). false 면 접속만 끊고 서보는 마지막 펄스를 계속 받는다
    /// ("연결 해제 시 출력 유지" 옵션). 프로그램 종료 때는 ServoModel 이 항상 true 로 되돌린다.
    /// </summary>
    bool ReleaseOnDispose { get; set; }

    void Open(int gpio);                 // 핀 준비 (원격이면 접속)
    void WritePulse(int microseconds);   // 펄스폭 출력 (0 = 끔)
    void Release();                      // 펄스 중지 → 서보가 힘을 빼고 멈춤

    /// <summary>
    /// 장치가 지금 내보내고 있는 펄스폭(µs)을 읽는다. 0 = 꺼짐, null = 알 수 없음(시뮬레이션, 서보로 쓴 적 없는 핀 등).
    /// 연결 직후 화면을 실제 서보 상태에 맞추는 데 쓴다.
    /// </summary>
    int? ReadPulse();
}
