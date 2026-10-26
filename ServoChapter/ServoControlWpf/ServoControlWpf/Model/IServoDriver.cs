namespace ServoControlWpf.Model;

/// <summary>
/// [Model] 서보에 펄스를 내보내는 하드웨어 계층의 공통 인터페이스 (Strategy 패턴)
///   - RemotePigpioDriver : TCP로 Pi의 pigpiod에 명령 전송 (실제 서보) — 직접 연결 / WSL 터널 둘 다 사용
///   - SimulatedDriver    : 연결 전·테스트용, 화면만 동작
/// 실패는 <see cref="ServoException"/> 으로 알린다.
/// </summary>
public interface IServoDriver : IDisposable
{
    string Name { get; }                 // 상태 표시용 이름
    string Info { get; }                 // 연결된 장치 정보 (Pi 리비전 등)
    bool IsRemote { get; }
    bool IsLinkAlive { get; }            // 통신 경로가 살아 있는지

    void Open(int gpio);                 // 핀 준비 (원격이면 접속)
    void WritePulse(int microseconds);   // 펄스폭 출력 (0 = 끔)
    void Release();                      // 펄스 중지 → 서보가 힘을 빼고 멈춤
}
