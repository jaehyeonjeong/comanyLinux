using System.Diagnostics;
using System.IO;
using System.Text;

namespace ServoControlWpf.Services;

/// <summary>
/// WPF 가 WSL(리눅스) 에 접근하는 창구. wsl.exe 를 실행해서
///   - 배포판 목록 확인, 사용 가능 여부 확인
///   - 기존 셸 스크립트(pi_setup.sh / deploy.sh / pi_info.sh) 실행
///   - ssh 별명(rpi) 으로 Pi 에 명령 실행
/// 을 한다. 주의: `wsl -l` 류의 관리 명령은 UTF-16LE 로 출력하고, 리눅스 명령 출력은 UTF-8 이다.
/// </summary>
public sealed class WslService
{
    public static string WslExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static bool WslExeExists => File.Exists(WslExePath);

    public sealed record Probe(bool Available, string Message, IReadOnlyList<string> Distros);

    /// <summary>WSL 이 설치되어 있고 배포판이 하나 이상 있는지 확인</summary>
    public static async Task<Probe> ProbeAsync(CancellationToken ct = default)
    {
        if (!WslExeExists)
            return new Probe(false, "이 PC에 WSL(wsl.exe)이 없습니다. 'wsl --install' 로 설치하세요.", Array.Empty<string>());

        try
        {
            var r = await ProcessRunner.RunAsync(WslExePath, new[] { "-l", "-q" }, Encoding.Unicode, ct: ct);
            var distros = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (r.ExitCode != 0 || distros.Count == 0)
            {
                var msg = (r.StdOut + r.StdErr).Trim();
                return new Probe(false,
                    "이 PC에 WSL이 설치되어 있지 않거나 배포판이 없습니다.\n" +
                    (msg.Length > 0 ? msg : "'wsl --install -d Ubuntu' 로 설치하세요."), Array.Empty<string>());
            }
            return new Probe(true, $"WSL 사용 가능 · 배포판 {distros.Count}개", distros);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new Probe(false, "wsl.exe 실행 실패: " + ex.Message, Array.Empty<string>());
        }
    }

    private static List<string> BaseArgs(string? distro)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(distro))
        {
            args.Add("-d");
            args.Add(distro.Trim());
        }
        return args;
    }

    /// <summary>
    /// 스크립트 폴더에서 `bash ./스크립트` 를 실행한다. 폴더는 Windows 경로(D:\...) 든 WSL 경로(/home/..., ~/...) 든 된다.
    /// (--cd 가 Windows 경로를 /mnt/d/... 로 자동 변환)
    /// </summary>
    public static Task<ProcessRunner.Result> RunScriptAsync(
        string? distro, string scriptDir, string scriptName, IEnumerable<string>? scriptArgs,
        Action<string, bool> onLine, CancellationToken ct)
    {
        var args = BaseArgs(distro);
        args.Add("--cd");
        args.Add(scriptDir);
        args.Add("-e");
        args.Add("bash");
        args.Add("./" + scriptName);
        if (scriptArgs is not null)
            args.AddRange(scriptArgs);
        return ProcessRunner.RunAsync(WslExePath, args, Encoding.UTF8, onLine, ct);
    }

    /// <summary>WSL 안의 ssh 로 Pi 에 명령 한 줄을 실행한다 (비밀번호 입력은 받을 수 없으므로 키 인증 필요).</summary>
    public static Task<ProcessRunner.Result> RunSshAsync(
        string? distro, string sshAlias, string remoteCommand,
        Action<string, bool> onLine, CancellationToken ct)
    {
        var args = BaseArgs(distro);
        args.AddRange(new[]
        {
            "-e", "ssh",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=5",
            sshAlias,
            remoteCommand,
        });
        return ProcessRunner.RunAsync(WslExePath, args, Encoding.UTF8, onLine, ct);
    }

    /// <summary>WSL 안에서 임의의 리눅스 명령을 실행한다 (예: cat pi_info.txt).</summary>
    public static Task<ProcessRunner.Result> RunShellAsync(
        string? distro, string? workDir, string command,
        Action<string, bool>? onLine, CancellationToken ct)
    {
        var args = BaseArgs(distro);
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            args.Add("--cd");
            args.Add(workDir);
        }
        args.AddRange(new[] { "-e", "bash", "-c", command });
        return ProcessRunner.RunAsync(WslExePath, args, Encoding.UTF8, onLine, ct);
    }

    /// <summary>
    /// 스크립트를 **보이는 터미널 창**에서 실행한다.
    /// pi_setup.sh 처럼 sudo 비밀번호나 ssh 확인 질문이 나올 수 있는 스크립트는 콘솔이 있어야 하기 때문.
    /// GUI 프로그램에서 콘솔 프로그램을 창 숨김 없이 실행하면 Windows 가 새 콘솔 창을 만들어 준다.
    /// </summary>
    public static void OpenScriptInTerminal(string? distro, string scriptDir, string scriptName, IEnumerable<string>? scriptArgs = null)
    {
        var psi = new ProcessStartInfo(WslExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var a in BaseArgs(distro))
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--cd");
        psi.ArgumentList.Add(scriptDir);
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        var extra = scriptArgs is null ? "" : " " + string.Join(" ", scriptArgs.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
        psi.ArgumentList.Add(
            $"./{scriptName}{extra}; rc=$?; echo; echo \"[종료 코드 $rc] 아무 키나 누르면 창이 닫힙니다.\"; read -rsn1");
        Process.Start(psi);
    }
}
