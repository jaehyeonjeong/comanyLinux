namespace ServoControlWpf.Model;

/// <summary>
/// 드라이버가 명령을 수행하지 못했을 때 던지는 예외.
/// Qt 코드는 bool 반환 + QString* error 였지만, C#에서는 예외로 바꾸고
/// "통신이 끊긴 오류인지"를 LinkLost 로 함께 전달한다.
/// (Controller 는 LinkLost 이면 연결을 정리하고, pigpio 가 거부한 오류이면 연결을 유지한다.)
/// </summary>
public sealed class ServoException : Exception
{
    public bool LinkLost { get; }

    public ServoException(string message, bool linkLost = false, Exception? inner = null)
        : base(message, inner)
    {
        LinkLost = linkLost;
    }
}
