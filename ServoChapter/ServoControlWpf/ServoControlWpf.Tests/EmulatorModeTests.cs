using System.Net;
using System.Net.Sockets;
using ServoControlWpf.Model;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 에뮬레이터 모드 [연결] 을 ViewModel 수준에서 통째로: 포트가 비어 있을 때와, 다른 프로그램이 그 포트를 이미 쓰고 있을 때.
/// (실제로 겪은 문제: WSL 의 fake_pigpiod.py 가 8888 을 듣고 있으면 WSL2 가 그 포트를 Windows 127.0.0.1 로도 열어 앱 안 에뮬레이터가 못 뜸)
/// </summary>
public class EmulatorModeTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static async Task<bool> WaitAsync(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private static MainViewModel MakeVm(int port, out FakeDialogs dlg)
    {
        var model = new ServoModel(new SimulatedDriver());
        model.Start(ServoSpec.Hs311Official());
        dlg = new FakeDialogs();
        var vm = new MainViewModel(model, AppSettings.InMemory(), dlg) { Port = port, ModeIndex = 2 };   // 2 = 에뮬레이터
        return vm;
    }

    [Fact]
    public async Task Emulator_Connects_OnRequestedPort()
    {
        var port = FreePort();
        var vm = MakeVm(port, out var dlg);
        vm.ConnectToggleCommand.Execute(null);
        Assert.True(await WaitAsync(() => vm.IsConnected), "에뮬레이터에 연결돼야 함");
        Assert.Null(vm.EmulatorFallbackPort);
        Assert.Contains($"127.0.0.1:{port}", vm.DriverName);
        Assert.Empty(dlg.Errors);
        Assert.False(vm.IsBusy);
        vm.Shutdown();
    }

    [Fact]
    public async Task Emulator_FallsBackToFreePort_WhenPortIsBusy()
    {
        var port = FreePort();
        var blocker = new TcpListener(IPAddress.Loopback, port);
        blocker.Start();                                   // 다른 프로그램이 포트를 쓰는 상황
        try
        {
            var vm = MakeVm(port, out var dlg);
            vm.ConnectToggleCommand.Execute(null);
            Assert.True(await WaitAsync(() => vm.IsConnected), "임시 포트로라도 연결돼야 함");
            Assert.NotNull(vm.EmulatorFallbackPort);
            Assert.NotEqual(port, vm.EmulatorFallbackPort);
            Assert.Contains("임시 포트", vm.EmulatorText);
            // IsConnected 는 SetDriver 직후 true 가 되고 상태 줄 안내는 그 뒤에 적히므로 잠깐 기다림 (경쟁으로 가끔 실패하던 곳)
            Assert.True(await WaitAsync(() => vm.StatusText.Contains("임시 포트")), "상태 줄에 임시 포트 안내: " + vm.StatusText);
            Assert.Empty(dlg.Errors);                      // 오류 창 없이 조용히 해결
            Assert.False(vm.IsBusy);

            // 실제로 임시 포트에서 pigpiod 규약으로 응답하는지
            using var c = new PigpioClient("127.0.0.1", vm.EmulatorFallbackPort!.Value);
            c.Connect();
            Assert.Equal(79, c.Command(PigpioClient.CmdPigpv, 0, 0));
            vm.Shutdown();
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("")]
    public async Task DirectMode_RejectsOwnAddress_AndPointsToEmulator(string host)
    {
        var model = new ServoModel(new SimulatedDriver());
        model.Start(ServoSpec.Hs311Official());
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(model, AppSettings.InMemory(), dlg) { Host = host, Port = FreePort(), ModeIndex = 0 };
        vm.ConnectToggleCommand.Execute(null);
        Assert.True(await WaitAsync(() => dlg.Errors.Count > 0, 5000));
        Assert.Contains("에뮬레이터", dlg.Errors[0]);
        Assert.False(vm.IsConnected);
        vm.Shutdown();
    }

    [Theory]
    [InlineData("192.168.10.2", false)]
    [InlineData("172.30.1.81", false)]
    [InlineData("raspberrypi.local", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.5.5.5", true)]
    [InlineData("LOCALHOST", true)]
    public void IsLoopbackHost_Classifies(string host, bool loopback)
        => Assert.Equal(loopback, MainViewModel.IsLoopbackHost(host));

    [Fact]
    public async Task ConnectFailure_LeavesButtonUsable_BeforeDialog()
    {
        // 직접 연결로 아무도 듣지 않는 포트 → 실패. 오류 창이 뜨기 전에 IsBusy 가 풀려 있어야 함
        var port = FreePort();
        var model = new ServoModel(new SimulatedDriver());
        model.Start(ServoSpec.Hs311Official());
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(model, AppSettings.InMemory(), dlg) { Host = "127.0.0.1", Port = port, ModeIndex = 0 };
        var busyWhenDialogShown = true;
        dlg.OnError = () => busyWhenDialogShown = vm.IsBusy;
        vm.ConnectToggleCommand.Execute(null);
        Assert.True(await WaitAsync(() => dlg.Errors.Count > 0, 8000), "오류 창이 떠야 함");
        Assert.False(busyWhenDialogShown);
        Assert.False(vm.IsConnected);
        Assert.Equal("연결", vm.ConnectButtonText);
        vm.Shutdown();
    }
}
