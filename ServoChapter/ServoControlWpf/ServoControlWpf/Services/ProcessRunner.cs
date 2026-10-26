using System.Diagnostics;
using System.Text;

namespace ServoControlWpf.Services;

/// <summary>
/// 외부 프로그램(wsl.exe 등)을 실행하고 출력 줄을 실시간으로 받아 오는 도우미.
/// 창을 띄우지 않고 stdout/stderr 를 가로채며, 취소하면 프로세스 트리를 통째로 종료한다.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// 프로그램을 실행하고 끝날 때까지 기다린다. 출력 줄마다 onLine(줄, 오류출력인지) 을 호출한다.
    /// </summary>
    public static async Task<Result> RunAsync(
        string fileName,
        IEnumerable<string> args,
        Encoding? outputEncoding = null,
        Action<string, bool>? onLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            var line = e.Data.Replace("\0", "");
            stdout.AppendLine(line);
            onLine?.Invoke(line, false);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            var line = e.Data.Replace("\0", "");
            stderr.AppendLine(line);
            onLine?.Invoke(line, true);
        };

        proc.Start();
        proc.StandardInput.Close();   // 입력을 기다리며 멈추지 않게 함
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);
        return new Result(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
