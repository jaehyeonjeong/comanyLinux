using System.Diagnostics;
using ServoControlWpf.Model;
using ServoControlWpf.Services;
using Xunit;
using Xunit.Abstractions;

namespace ServoControlWpf.Tests;

/// <summary>
/// WSL 이 있는 PC 에서만 의미 있는 테스트: ssh 별명이 없을 때 터널 열기가 "빨리, 안내문과 함께" 실패해야 한다.
/// (WSL 이 없는 PC 에서는 WSL 없음 안내로 실패하는지만 본다)
/// </summary>
public class WslSshTunnelTests
{
    private readonly ITestOutputHelper _out;
    public WslSshTunnelTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Open_WithUnknownAlias_FailsFastWithMessage()
    {
        var sw = Stopwatch.StartNew();
        var progress = new Progress<string>(s => _out.WriteLine($"[{sw.ElapsedMilliseconds} ms] {s}"));

        var task = WslSshTunnel.OpenAsync(null, "no-such-host-zz", 18877, 18878, timeoutMs: 10000, progress: progress);
        var finished = await Task.WhenAny(task, Task.Delay(20000));
        Assert.True(finished == task, "OpenAsync 가 20초 안에 끝나지 않았습니다 (멈춤)");

        Exception? raw = null;
        try { await task; } catch (Exception e) { raw = e; }
        _out.WriteLine($"[{sw.ElapsedMilliseconds} ms] 예외 종류: {raw?.GetType().FullName}\n{raw}");
        var ex = Assert.IsType<ServoException>(raw);
        Assert.True(ex.LinkLost);
        if (WslService.WslExeExists && (await WslService.ProbeAsync()).Available)
        {
            Assert.True(sw.ElapsedMilliseconds < 9000, "ssh 가 바로 실패하는데도 시간 초과까지 기다렸습니다");
            Assert.Contains("ssh", ex.Message);
        }
    }
}
