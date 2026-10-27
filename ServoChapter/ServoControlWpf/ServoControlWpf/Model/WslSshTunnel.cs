using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ServoControlWpf.Services;

namespace ServoControlWpf.Model;

/// <summary>
/// [Model] "WSL 터널" 연결 모드의 핵심.
///
///   Windows WPF ──TCP 127.0.0.1:18888──▶ (WSL 안) ssh -L 18888:127.0.0.1:8888 rpi ──SSH──▶ Pi 의 sshd ──▶ 127.0.0.1:8888 pigpiod
///
/// wsl.exe 로 WSL 안에서 ssh 포트 포워딩을 띄운다. WSL2 는 WSL 안에서 127.0.0.1 에 열린 포트를 Windows 의 localhost 로도
/// 보여 주므로(localhost forwarding), WPF 는 그냥 127.0.0.1:18888 로 접속하면 된다.
/// Pi 쪽에서는 접속이 127.0.0.1 에서 오는 것으로 보이므로 pigpiod 의 원격 허용 설정(-n)이 없어도 된다.
/// </summary>
public sealed class WslSshTunnel : IDisposable
{
    private Process? _process;        // 우리가 띄운 wsl.exe (이미 열린 터널을 재사용했으면 null)
    private readonly StringBuilder _stderr = new();

    public string LocalHost => "127.0.0.1";
    public int LocalPort { get; }
    public string SshAlias { get; }
    public string? Distro { get; }
    public bool OwnsProcess => _process is not null;

    private WslSshTunnel(string? distro, string sshAlias, int localPort)
    {
        Distro = distro;
        SshAlias = sshAlias;
        LocalPort = localPort;
    }

    /// <summary>
    /// 터널을 열고 로컬 포트가 열릴 때까지 기다린다. 실패하면 ServoException(한글 안내 + ssh 오류 내용).
    /// </summary>
    public static async Task<WslSshTunnel> OpenAsync(
        string? distro, string sshAlias, int localPort, int remotePort = 8888,
        int timeoutMs = 10000, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var tunnel = new WslSshTunnel(distro, sshAlias, localPort);

        // 1) 이미 누가 열어 둔 터널이 있으면 그대로 사용 (앱이 비정상 종료돼 ssh 가 남아 있는 경우 등)
        if (await IsPortOpenAsync(localPort, 300).ConfigureAwait(false))
        {
            progress?.Report($"127.0.0.1:{localPort} 가 이미 열려 있어 기존 터널을 사용합니다.");
            return tunnel;
        }

        // 2) WSL 확인
        progress?.Report("WSL 확인 중…");
        var probe = await WslService.ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Available)
            throw new ServoException("WSL 터널을 열 수 없습니다.\n\n" + probe.Message, linkLost: true);
        if (!string.IsNullOrWhiteSpace(distro) && !probe.Distros.Contains(distro.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new ServoException(
                $"WSL 배포판 '{distro}' 이(가) 없습니다.\n설치된 배포판: {string.Join(", ", probe.Distros)}", linkLost: true);

        // 3) wsl.exe 안에서 ssh -N -L 실행
        progress?.Report($"WSL에서 ssh 터널 여는 중… (ssh -L {localPort}:127.0.0.1:{remotePort} {sshAlias})");
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
        if (!string.IsNullOrWhiteSpace(distro))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro.Trim());
        }
        foreach (var a in new[]
                 {
                     "-e", "ssh",
                     "-N",                                   // 명령 없이 포워딩만
                     "-o", "BatchMode=yes",                  // 비밀번호/확인 질문 금지 (키 인증 전제)
                     "-o", "ExitOnForwardFailure=yes",       // 포워딩 실패 시 바로 종료 → 오류를 빨리 알 수 있음
                     "-o", "ConnectTimeout=5",
                     "-o", "ServerAliveInterval=15",
                     "-L", $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}",
                     sshAlias,
                 })
            psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) tunnel._stderr.AppendLine(e.Data.Replace("\0", "")); };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) tunnel._stderr.AppendLine(e.Data.Replace("\0", "")); };
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
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        tunnel._process = proc;

        // 4) 로컬 포트가 열릴 때까지 폴링
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);   // stderr 마지막 줄 수집
                var err = tunnel._stderr.ToString().Trim();
                var exitCode = proc.ExitCode;                       // Dispose 전에 읽어 둠 (뒤에 읽으면 예외)
                tunnel.Dispose();
                throw new ServoException(
                    "WSL 안의 ssh 가 바로 종료되었습니다 (종료 코드 " + exitCode + ").\n\n" +
                    (err.Length > 0 ? err + "\n\n" : "") +
                    $"WSL 터미널에서 'ssh {sshAlias}' 가 비밀번호 없이 되는지(키 인증), ~/.ssh/config 에 Host {sshAlias} 가 있는지 확인하세요.",
                    linkLost: true);
            }
            if (await IsPortOpenAsync(localPort, 300).ConfigureAwait(false))
            {
                progress?.Report("터널 열림");
                return tunnel;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        var lastErr = tunnel._stderr.ToString().Trim();
        tunnel.Dispose();
        throw new ServoException(
            $"{timeoutMs / 1000}초 안에 터널 포트 127.0.0.1:{localPort} 가 열리지 않았습니다.\n" +
            (lastErr.Length > 0 ? "\n" + lastErr + "\n" : "") +
            "\nPi 가 켜져 있고 WSL 에서 'ssh " + sshAlias + "' 가 되는지 확인하세요.", linkLost: true);
    }

    /// <summary>127.0.0.1:port 에 TCP 접속이 되는지 (WslFakePi 도 함께 사용)</summary>
    internal static async Task<bool> IsPortOpenAsync(int port, int timeoutMs)
    {
        using var c = new TcpClient();
        try
        {
            var t = c.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(t, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return done == t && t.IsCompletedSuccessfully;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public string Describe() => $"WSL 터널 → {SshAlias}" + (string.IsNullOrWhiteSpace(Distro) ? "" : $" ({Distro})");

    public void Dispose()
    {
        var p = _process;
        _process = null;
        if (p is null)
            return;
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);   // wsl.exe 와 그 안의 ssh 까지
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }
}
