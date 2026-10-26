using System.Net;
using System.Net.Sockets;
using System.Text;
using PigpioEmulator;

// 가짜 pigpiod 콘솔 프로그램
//   사용법: pigpio-emu [포트] [바인드 주소]
//           pigpio-emu                → 127.0.0.1:8888  (같은 PC 의 WPF 앱 "직접 연결" 모드에서 Pi 주소 127.0.0.1)
//           pigpio-emu 8888 0.0.0.0   → 모든 주소        (WSL 의 Qt ServoControl 에서 Windows 호스트 IP 로 접속 가능)
// 받은 명령을 한 줄씩 보여 주고, 아래쪽에 흉내 낸 서보 위치를 막대로 그린다.

Console.OutputEncoding = Encoding.UTF8;
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8888;
var bind = args.Length > 1 ? args[1] : "127.0.0.1";

if (!IPAddress.TryParse(bind, out var bindIp))
{
    Console.Error.WriteLine($"바인드 주소가 올바르지 않습니다: {bind}");
    return 2;
}

// 상태 파일: WPF 앱의 에뮬레이터와 같은 파일을 써서 "같은 가짜 서보" 가 된다 (껐다 켜도 마지막 펄스·각도 유지)
using var server = new PigpioEmulatorServer(bindIp, port, PigpioEmulatorServer.DefaultStateFile);
var lastPulse = new Dictionary<int, int>();

server.CommandReceived += ev =>
{
    var mark = ev.Result < 0 ? "✗" : "✓";
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {mark} {ev.Description}");
};
server.ClientCountChanged += n => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ● 접속 수 {n}");

try
{
    server.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"포트 {port} 를 열 수 없습니다: {ex.Message}");
    return 1;
}

Console.WriteLine($"가짜 pigpiod 실행 중: {server.EndPoint}   (Ctrl+C 로 종료)");
Console.WriteLine("흉내 내는 명령: SERVO(8) HWVER(17)=0xc03111 PIGPV(26)=79 GPW(84) · 펄스 범위 0, 500~2500 µs");
Console.WriteLine($"상태 파일: {server.StateFile}  (껐다 켜도 마지막 펄스·각도 유지)");
foreach (var (savedGpio, savedPulse) in server.SnapshotPulses())
    Console.WriteLine($"  복원: GPIO{savedGpio} {(savedPulse == 0 ? "출력 꺼짐" : savedPulse + " µs")}, 각도 {server.GetSimulatedAngle(savedGpio):+0.0;-0.0}°");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// 서보 위치 막대: 펄스폭이나 흉내 낸 각도가 바뀌었을 때만 한 줄 그린다
var lastAngle = new Dictionary<int, double>();
while (!cts.IsCancellationRequested)
{
    await Task.Delay(100);
    foreach (var (gpio, pulse) in server.SnapshotPulses())
    {
        var angle = Math.Round(server.GetSimulatedAngle(gpio), 1);
        var target = pulse == 0 ? double.NaN : (pulse - 1500) / 10.0;
        var samePulse = lastPulse.TryGetValue(gpio, out var lp) && lp == pulse;
        var sameAngle = lastAngle.TryGetValue(gpio, out var la) && Math.Abs(la - angle) < 0.05;
        if (samePulse && sameAngle)
            continue;
        lastPulse[gpio] = pulse;
        lastAngle[gpio] = angle;

        const int width = 41;                         // −90° … +90° 를 41칸으로
        var idx = (int)Math.Round((Math.Clamp(angle, -90, 90) + 90) / 180.0 * (width - 1));
        var bar = new StringBuilder();
        for (var i = 0; i < width; i++) bar.Append(i == idx ? '█' : i == width / 2 ? '┼' : '─');
        var state = pulse == 0 ? "출력 꺼짐" : $"{pulse} µs → 목표 {target,6:+0.0;-0.0;0.0}°";
        Console.WriteLine($"          GPIO{gpio,-2} [{bar}] 현재 {angle,6:+0.0;-0.0;0.0}°  {state}");
    }
}

Console.WriteLine("종료");
return 0;
