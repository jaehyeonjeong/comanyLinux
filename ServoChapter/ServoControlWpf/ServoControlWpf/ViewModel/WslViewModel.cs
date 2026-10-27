using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ServoControlWpf.Model;
using ServoControlWpf.Services;

namespace ServoControlWpf.ViewModel;

/// <summary>
/// "WSL / Pi 관리" 탭. WPF 가 wsl.exe 를 통해 리눅스 셸 스크립트와 ssh 를 실행하고 결과를 로그 창에 보여 준다.
/// (기존 Qt 프로젝트의 pi_setup.sh / deploy.sh / pi_info.sh 를 그대로 재사용)
/// </summary>
public sealed class WslViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;

    public WslViewModel(AppSettings settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _distro = settings.WslDistro;
        _sshAlias = string.IsNullOrWhiteSpace(settings.SshAlias) ? "rpi" : settings.SshAlias;
        _scriptDir = string.IsNullOrWhiteSpace(settings.ScriptDir) ? FindDefaultScriptDir() : settings.ScriptDir;

        ProbeCommand = new RelayCommand(async () => await ProbeAsync(), () => !IsRunning);
        CheckPiCommand = new RelayCommand(async () => await RunSshAsync(
            "echo \"호스트: $(hostname)  IP: $(hostname -I)\"; " +
            "echo \"pigpiod: $(systemctl is-active pigpiod 2>/dev/null)  (pigpio $(dpkg-query -W -f='${Version}' pigpio 2>/dev/null || echo 미설치))\"; " +
            "echo \"온도: $(vcgencmd measure_temp 2>/dev/null)  throttled: $(vcgencmd get_throttled 2>/dev/null)\"; " +
            "echo \"가동 시간: $(uptime -p)\"; " +
            "echo \"8888 접속: $(ss -tn 2>/dev/null | grep -c ':8888 ')개\""), () => !IsRunning);
        RunPiInfoCommand = new RelayCommand(async () => await RunPiInfoAsync(), () => !IsRunning);
        OpenSetupTerminalCommand = new RelayCommand(OpenSetupTerminal, () => !IsRunning);
        RunSshCommand = new RelayCommand(async () =>
        {
            if (!string.IsNullOrWhiteSpace(SshCommandText))
                await RunSshAsync(SshCommandText.Trim());
        }, () => !IsRunning);
        RunDeployPiDisplayCommand = new RelayCommand(async () => await RunDeployPiDisplayAsync(), () => !IsRunning);
        OpenDeployPiDisplayTerminalCommand = new RelayCommand(OpenDeployPiDisplayTerminal, () => !IsRunning);
        PullRecordingsCommand = new RelayCommand(async () => await PullRecordingsAsync(), () => !IsRunning);
        InstallWatchCommand = new RelayCommand(async () => await RunWatchAsync("--install"), () => !IsRunning);
        WatchStatusCommand = new RelayCommand(async () => await RunWatchAsync("--status"), () => !IsRunning);
        _recordingsDir = string.IsNullOrWhiteSpace(settings.RecordingsDir) ? AppSettings.DefaultRecordingsDir : settings.RecordingsDir;
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearLogCommand = new RelayCommand(() => { Log.Clear(); OnPropertyChanged(nameof(LogText)); });
        BrowseScriptDirCommand = new RelayCommand(() =>
        {
            var dir = _dialogs.PickFolder("pi_setup.sh · deploy.sh · pi_info.sh 가 있는 폴더 선택",
                Directory.Exists(ScriptDir) ? ScriptDir : null);
            if (dir is not null) ScriptDir = dir;
        });
    }

    // ───────── 설정 ─────────
    private string _distro;
    private string _sshAlias;
    private string _scriptDir;
    private string _sshCommandText = "pigs hwver; pigs pigpv";

    public ObservableCollection<string> Distros { get; } = new();

    /// <summary>비우면 WSL 기본 배포판</summary>
    public string Distro { get => _distro; set => Set(ref _distro, value ?? ""); }
    public string SshAlias { get => _sshAlias; set => Set(ref _sshAlias, string.IsNullOrWhiteSpace(value) ? "rpi" : value.Trim()); }
    public string ScriptDir { get => _scriptDir; set { if (Set(ref _scriptDir, value)) _settings.ScriptDir = value; } }
    public string SshCommandText { get => _sshCommandText; set => Set(ref _sshCommandText, value); }

    private string _recordingsDir;
    /// <summary>Pi 의 녹화·사진을 모아 둘 WSL 폴더 (pull_recordings.sh 의 목적지). 설정에 저장.</summary>
    public string RecordingsDir
    {
        get => _recordingsDir;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? AppSettings.DefaultRecordingsDir : value.Trim();
            if (Set(ref _recordingsDir, v)) { _settings.RecordingsDir = v; _settings.Save(); }
        }
    }

    private string _lastPullSummary = "";
    /// <summary>마지막 가져오기 결과 한 줄 ("새 파일 1개 → ~/MyJob/…/recordings")</summary>
    public string LastPullSummary { get => _lastPullSummary; private set => Set(ref _lastPullSummary, value); }

    // ───────── 상태 ─────────
    private bool _isRunning;
    private string _wslStatus = "WSL 상태: 아직 확인 안 함 — [WSL 확인]을 누르세요";
    private bool _isWslAvailable;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                foreach (var c in new[] { ProbeCommand, CheckPiCommand, RunPiInfoCommand, OpenSetupTerminalCommand, RunSshCommand, CancelCommand, RunDeployPiDisplayCommand, OpenDeployPiDisplayTerminalCommand, PullRecordingsCommand, InstallWatchCommand, WatchStatusCommand })
                    c.RaiseCanExecuteChanged();
            }
        }
    }
    public string WslStatus { get => _wslStatus; private set => Set(ref _wslStatus, value); }
    public bool IsWslAvailable { get => _isWslAvailable; private set => Set(ref _isWslAvailable, value); }

    public ObservableCollection<string> Log { get; } = new();
    public string LogText => string.Join(Environment.NewLine, Log);

    // ───────── 커맨드 ─────────
    public RelayCommand ProbeCommand { get; }
    public RelayCommand CheckPiCommand { get; }
    public RelayCommand RunPiInfoCommand { get; }
    public RelayCommand OpenSetupTerminalCommand { get; }
    public RelayCommand RunSshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand BrowseScriptDirCommand { get; }
    /// <summary>[Pi 화면 배포]: pi_display/deploy_pi_display.sh 를 인라인 실행 (rsync → 실행 → 상태 확인)</summary>
    public RelayCommand RunDeployPiDisplayCommand { get; }
    /// <summary>[Pi 화면 배포(+apt) ↗]: 처음 한 번, apt 설치가 있어 sudo 비밀번호를 물을 수 있으므로 새 터미널 창에서</summary>
    public RelayCommand OpenDeployPiDisplayTerminalCommand { get; }
    /// <summary>[녹화 가져오기]: pi_display/pull_recordings.sh ssh별명 목적지 — Pi 의 새 녹화·사진을 rsync 로 WSL 폴더에</summary>
    public RelayCommand PullRecordingsCommand { get; }
    /// <summary>[감시 서비스 설치]: watch_recordings.sh --install — WPF 가 꺼져 있어도 30 초마다 가져오는 systemd user 서비스</summary>
    public RelayCommand InstallWatchCommand { get; }
    /// <summary>[감시 상태]: watch_recordings.sh --status</summary>
    public RelayCommand WatchStatusCommand { get; }

    /// <summary>pi_display 폴더 (실행 파일 위치에서 위로 올라가며 deploy_pi_display.sh 를 찾음). 없으면 "".</summary>
    public string PiDisplayDir { get; } = FindPiDisplayDir();

    // ═════════ 동작 ═════════

    public async Task ProbeAsync()
    {
        IsRunning = true;
        try
        {
            Append("$ wsl -l -q");
            var p = await WslService.ProbeAsync();
            IsWslAvailable = p.Available;
            WslStatus = "WSL 상태: " + p.Message.Split('\n')[0];
            Distros.Clear();
            foreach (var d in p.Distros) Distros.Add(d);
            Append(p.Available ? "배포판: " + string.Join(", ", p.Distros) : p.Message, !p.Available);
            if (p.Available && !string.IsNullOrWhiteSpace(Distro) && !p.Distros.Contains(Distro, StringComparer.OrdinalIgnoreCase))
                Append($"주의: 설정된 배포판 '{Distro}' 이(가) 목록에 없습니다. 비워 두면 기본 배포판을 사용합니다.", true);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task RunSshAsync(string remoteCommand)
    {
        await RunGuardedAsync($"$ wsl -e ssh {SshAlias} \"{Shorten(remoteCommand)}\"",
            ct => WslService.RunSshAsync(DistroOrNull, SshAlias, remoteCommand, OnLine, ct));
    }

    private async Task RunScriptAsync(string script)
    {
        if (!await EnsureScriptDirAsync(script)) return;
        await RunGuardedAsync($"$ wsl --cd \"{ScriptDir}\" bash ./{script}",
            ct => WslService.RunScriptAsync(DistroOrNull, ScriptDir, script, null, OnLine, ct));
    }

    private async Task RunPiInfoAsync()
    {
        if (!await EnsureScriptDirAsync("pi_info.sh")) return;
        var ok = await RunGuardedAsync($"$ wsl --cd \"{ScriptDir}\" bash ./pi_info.sh {SshAlias}",
            ct => WslService.RunScriptAsync(DistroOrNull, ScriptDir, "pi_info.sh", new[] { SshAlias }, OnLine, ct));
        if (!ok) return;
        Append("───── pi_info.txt ─────");
        await RunGuardedAsync(null,
            ct => WslService.RunShellAsync(DistroOrNull, ScriptDir, "cat pi_info.txt", OnLine, ct));
    }

    private void OpenSetupTerminal()
    {
        if (!WslService.WslExeExists)
        {
            _dialogs.ShowError("이 PC에 WSL(wsl.exe)이 없습니다.");
            return;
        }
        if (!ScriptLooksPresent("pi_setup.sh"))
        {
            _dialogs.ShowError($"'{ScriptDir}' 에 pi_setup.sh 가 없습니다. 스크립트 폴더를 확인하세요.");
            return;
        }
        try
        {
            WslService.OpenScriptInTerminal(DistroOrNull, ScriptDir, "pi_setup.sh", new[] { SshAlias });
            Append($"새 터미널 창에서 pi_setup.sh {SshAlias} 를 실행합니다 (sudo 비밀번호를 물을 수 있음).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError("터미널 창을 열 수 없습니다: " + ex.Message);
        }
    }

    private async Task RunDeployPiDisplayAsync()
    {
        if (PiDisplayDir.Length == 0)
        {
            _dialogs.ShowError("pi_display 폴더(deploy_pi_display.sh)를 찾지 못했습니다. 프로그램 폴더 구조를 확인하세요.");
            return;
        }
        await RunGuardedAsync($"$ wsl --cd \"{PiDisplayDir}\" bash ./deploy_pi_display.sh {SshAlias}",
            ct => WslService.RunScriptAsync(DistroOrNull, PiDisplayDir, "deploy_pi_display.sh", new[] { SshAlias }, OnLine, ct));
    }

    private void OpenDeployPiDisplayTerminal()
    {
        if (!WslService.WslExeExists) { _dialogs.ShowError("이 PC에 WSL(wsl.exe)이 없습니다."); return; }
        if (PiDisplayDir.Length == 0) { _dialogs.ShowError("pi_display 폴더(deploy_pi_display.sh)를 찾지 못했습니다."); return; }
        try
        {
            WslService.OpenScriptInTerminal(DistroOrNull, PiDisplayDir, "deploy_pi_display.sh", new[] { SshAlias, "--install-deps", "--autostart" });
            Append($"새 터미널 창에서 deploy_pi_display.sh {SshAlias} --install-deps --autostart 를 실행합니다 (Pi 의 sudo 비밀번호를 물을 수 있음).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dialogs.ShowError("터미널 창을 열 수 없습니다: " + ex.Message);
        }
    }

    /// <summary>
    /// Pi → WSL 녹화 가져오기. 스크립트의 마지막 줄 "PULLED n" 을 읽어 (성공 여부, 새 파일 수) 를 돌려준다.
    /// 다른 작업이 돌고 있으면 (false, -1).
    /// </summary>
    public async Task<(bool ok, int pulled)> PullRecordingsAsync()
    {
        if (PiDisplayDir.Length == 0)
        {
            _dialogs.ShowError("pi_display 폴더(pull_recordings.sh)를 찾지 못했습니다. 프로그램 폴더 구조를 확인하세요.");
            return (false, -1);
        }
        if (IsRunning)
            return (false, -1);
        var lines = new List<string>();
        var ok = await RunGuardedAsync($"$ wsl --cd \"{PiDisplayDir}\" bash ./pull_recordings.sh {SshAlias} {RecordingsDir}",
            ct => WslService.RunScriptAsync(DistroOrNull, PiDisplayDir, "pull_recordings.sh", new[] { SshAlias, RecordingsDir },
                (line, err) => { lock (lines) lines.Add(line); OnLine(line, err); }, ct));
        int pulled;
        lock (lines) pulled = ParsePulled(lines);
        LastPullSummary = ok
            ? (pulled > 0 ? $"새 파일 {pulled}개 → {RecordingsDir}" : $"새 파일 없음 ({RecordingsDir})")
            : "가져오기 실패 — 로그 확인 (Pi 가 켜져 있고 ssh 별명이 맞는지)";
        return (ok, pulled);
    }

    /// <summary>pull_recordings.sh 출력에서 "PULLED n" 줄을 찾아 n 을 돌려준다 (없으면 -1)</summary>
    public static int ParsePulled(IEnumerable<string> lines)
    {
        var n = -1;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("PULLED ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(7).Trim(), out var v))
                n = v;
        }
        return n;
    }

    private async Task RunWatchAsync(string action)
    {
        if (PiDisplayDir.Length == 0)
        {
            _dialogs.ShowError("pi_display 폴더(watch_recordings.sh)를 찾지 못했습니다.");
            return;
        }
        var args = action == "--install" ? new[] { SshAlias, RecordingsDir, "--install" } : new[] { SshAlias, RecordingsDir, action };
        await RunGuardedAsync($"$ wsl --cd \"{PiDisplayDir}\" bash ./watch_recordings.sh {string.Join(' ', args)}",
            ct => WslService.RunScriptAsync(DistroOrNull, PiDisplayDir, "watch_recordings.sh", args, OnLine, ct));
    }

    /// <summary>WSL 경로(~/… 또는 /home/…)를 Windows 탐색기가 열 수 있는 경로로 (wslpath -w). 실패하면 null.</summary>
    public async Task<string?> ToWindowsPathAsync(string wslPath, CancellationToken ct = default)
    {
        if (!WslService.WslExeExists) return null;
        var lines = new List<string>();
        try
        {
            var r = await WslService.RunShellAsync(DistroOrNull, null,
                "p=\"" + wslPath.Replace("\"", "\\\"") + "\"; p=\"${p/#\\~/$HOME}\"; mkdir -p \"$p\" && wslpath -w \"$p\"",
                (l, e) => { if (!e) lock (lines) lines.Add(l); }, ct);
            if (r.ExitCode != 0) return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        lock (lines) return lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
    }

    private static string FindPiDisplayDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "pi_display");
                if (File.Exists(Path.Combine(candidate, "deploy_pi_display.sh")))
                    return candidate;
            }
        }
        catch (IOException) { }
        catch (System.Security.SecurityException) { }
        return "";
    }

    /// <summary>작업 하나를 실행하면서 로그·실행 중 표시·취소를 공통 처리. 성공(종료 코드 0)이면 true.</summary>
    private async Task<bool> RunGuardedAsync(string? header, Func<CancellationToken, Task<ProcessRunner.Result>> work)
    {
        if (!WslService.WslExeExists)
        {
            Append("이 PC에 WSL(wsl.exe)이 없습니다. 'wsl --install' 로 설치하세요.", true);
            return false;
        }
        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            if (header is not null) Append(header);
            var r = await work(_cts.Token);
            if (r.ExitCode != 0)
                Append($"[종료 코드 {r.ExitCode}]", true);
            return r.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            Append("[취소됨]", true);
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append("[실행 실패] " + ex.Message, true);
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private async Task<bool> EnsureScriptDirAsync(string script)
    {
        if (string.IsNullOrWhiteSpace(ScriptDir))
        {
            _dialogs.ShowError("스크립트 폴더가 비어 있습니다. pi_setup.sh·pi_info.sh 가 있는 폴더(기본 tools\\pi)를 지정하세요.");
            return false;
        }
        if (ScriptLooksPresent(script))
            return true;
        if (IsWslPath(ScriptDir))
            return true;   // WSL 내부 경로는 여기서 확인할 수 없으니 실행해 보고 결과로 판단
        _dialogs.ShowError($"'{ScriptDir}' 에 {script} 가 없습니다. 스크립트 폴더를 확인하세요.");
        return await Task.FromResult(false);
    }

    private bool ScriptLooksPresent(string script)
        => IsWslPath(ScriptDir) || File.Exists(Path.Combine(ScriptDir, script));

    private static bool IsWslPath(string dir) => dir.StartsWith('/') || dir.StartsWith('~');

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private string? DistroOrNull => string.IsNullOrWhiteSpace(Distro) ? null : Distro.Trim();

    private void OnLine(string line, bool isErr) => _ui.Post(_ => Append(line, isErr), null);

    private void Append(string line, bool isErr = false)
    {
        Log.Add(isErr ? "! " + line : line);
        if (Log.Count > 2000) Log.RemoveAt(0);
        OnPropertyChanged(nameof(LogText));
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "…";

    /// <summary>
    /// 실행 파일 위치에서 위로 올라가며 Pi 관리 스크립트(pi_setup.sh·pi_info.sh) 폴더를 찾는다 (개발 환경 기본값).
    /// 1순위 tools\pi (이 프로젝트 안), 2순위 예전 ServoControl 폴더.
    /// </summary>
    private static string FindDefaultScriptDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            {
                foreach (var rel in new[] { Path.Combine("tools", "pi"), "ServoControl" })
                {
                    var candidate = Path.Combine(dir.FullName, rel);
                    if (File.Exists(Path.Combine(candidate, "pi_setup.sh")))
                        return candidate;
                }
            }
        }
        catch (IOException) { }
        catch (System.Security.SecurityException) { }
        return "";
    }
}
