using System.Diagnostics;
using System.IO;
using System.Text;
using ServoControlWpf.Services;

namespace ServoControlWpf.Model;

/// <summary>
/// [Model] 연결 방식 "에뮬레이터 → WSL 가짜 Pi": WSL 안에서 tools/fake_pigpiod.py(리눅스 가짜 pigpiod)를 띄우고 그 포트로 접속한다.
///
///   Windows WPF ──TCP 127.0.0.1:8888──▶ (WSL2 localhost 포워딩) ──▶ WSL 의 python3 fake_pigpiod.py
///
/// 명령이 실제로 Windows → 가상 네트워크 → 리눅스 프로세스로 건너가므로 "앱 안 가짜 Pi" 보다 실제 Pi 에 가까운 연습이 된다.
/// 이미 그 포트가 열려 있으면(사용자가 ./run-from-wsl.sh fakepi 로 띄워 둠) 새로 띄우지 않고 그대로 쓴다.
/// 우리가 띄운 프로세스는 Dispose 때 종료한다(상태 파일 ~/.config/VI/fake_pigpiod.json 덕분에 다음에 같은 자리에서 다시 뜸).
/// </summary>
public sealed class WslFakePi : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _stderr = new();

    public int Port { get; }
    public bool OwnsProcess => _process is not null;
    /// <summary>가짜 Pi 가 stdout 에 찍은 마지막 줄 (예 "12:34:56  ✓ SERVO gpio=18 1700 µs")</summary>
    public string LastLine { get; private set; } = "";
    public event Action<string>? LineReceived;

    private WslFakePi(int port) => Port = port;

    /// <summary>실행 파일 위치에서 위로 올라가며 tools\fake_pigpiod.py 를 찾는다. 없으면 null.</summary>
    public static string? FindScript()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", "fake_pigpiod.py");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (IOException) { }
        catch (System.Security.SecurityException) { }
        return null;
    }

    public static async Task<WslFakePi> OpenAsync(string? distro, int port, IProgress<string>? progress = null,
        int timeoutMs = 10000, CancellationToken ct = default)
    {
        var fp = new WslFakePi(port);

        // 1) 이미 누가 띄워 두었으면 그대로 사용
        if (await WslSshTunnel.IsPortOpenAsync(port, 300).ConfigureAwait(false))
        {
            progress?.Report($"127.0.0.1:{port} 가 이미 열려 있어 그 가짜 Pi 를 사용합니다 (직접 띄운 fake_pigpiod.py 또는 pigpio-emu).");
            return fp;
        }

        // 2) 스크립트·WSL 확인
        var script = FindScript()
            ?? throw new ServoException("tools\\fake_pigpiod.py 를 찾지 못했습니다. 프로그램 폴더 구조(ServoControlWpf\\tools)를 확인하세요.", linkLost: true);
        progress?.Report("WSL 확인 중…");
        var probe = await WslService.ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Available)
            throw new ServoException("WSL 가짜 Pi 를 띄울 수 없습니다.\n\n" + probe.Message + "\n\n(WSL 없이 연습하려면 에뮬레이터 종류를 '앱 안 가짜 Pi' 로 바꾸세요)", linkLost: true);

        // 3) wsl.exe --cd <tools 폴더> -e python3 fake_pigpiod.py --port N --bind 0.0.0.0
        progress?.Report($"WSL 에서 fake_pigpiod.py 띄우는 중… (포트 {port})");
        var psi = new ProcessStartInfo(WslService.WslExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(distro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro.Trim()); }
        psi.ArgumentList.Add("--cd");
        psi.ArgumentList.Add(Path.GetDirectoryName(script)!);     // Windows 경로 → wsl 이 /mnt/... 로 변환
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("python3");
        psi.ArgumentList.Add("fake_pigpiod.py");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--bind"); psi.ArgumentList.Add("0.0.0.0");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data.Replace("\0", "").Trim();
            if (line.Length == 0) return;
            fp.LastLine = line;
            fp.LineReceived?.Invoke(line);
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) fp._stderr.AppendLine(e.Data.Replace("\0", "")); };
        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            proc.Dispose();
            throw new ServoException("wsl.exe 를 실행할 수 없습니다: " + ex.Message, linkLost: true, inner: ex);
        }
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        fp._process = proc;

        // 4) 포트가 열릴 때까지
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var err = fp._stderr.ToString().Trim();
                var code = proc.ExitCode;
                fp.Dispose();
                throw new ServoException(
                    $"WSL 의 fake_pigpiod.py 가 바로 종료되었습니다 (종료 코드 {code}).\n\n" + (err.Length > 0 ? err + "\n\n" : "") +
                    "WSL 에 python3 가 있는지, 포트가 비어 있는지 확인하세요.", linkLost: true);
            }
            if (await WslSshTunnel.IsPortOpenAsync(port, 300).ConfigureAwait(false))
            {
                progress?.Report("WSL 가짜 Pi 준비됨");
                return fp;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        var lastErr = fp._stderr.ToString().Trim();
        fp.Dispose();
        throw new ServoException($"{timeoutMs / 1000}초 안에 WSL 가짜 Pi 의 포트 127.0.0.1:{port} 가 열리지 않았습니다." +
                                 (lastErr.Length > 0 ? "\n\n" + lastErr : ""), linkLost: true);
    }

    public string Describe() => OwnsProcess ? $"WSL 가짜 Pi (fake_pigpiod.py) 127.0.0.1:{Port}" : $"WSL 가짜 Pi (이미 떠 있던 것) 127.0.0.1:{Port}";

    public void Dispose()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }
}
