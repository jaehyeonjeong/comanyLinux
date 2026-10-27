using System.Reflection;

namespace ServoControlWpf.Services;

/// <summary>
/// 프로그램 버전. 빌드 때 Directory.Build.props 가 VERSION 파일을 읽어 어셈블리에 박아 둔 값을 실행 중에 꺼낸다.
///   Informational : "1.1.0" (프리릴리스면 "1.2.0-rc1" 처럼 그대로)
///   Short         : '+' 뒤(빌드 메타데이터)를 뗀 것
/// </summary>
public static class AppVersion
{
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string Short
    {
        get
        {
            var v = Informational;
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }
}
