using ServoControlWpf.Model;
using ServoControlWpf.Services;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>웹캠 + 네트워크(Pi) 카메라를 하나로 묶는 CompositeCameraSource 와, 그것을 쓰는 CameraViewModel</summary>
public class CompositeCameraSourceTests
{
    private static readonly CameraDevice Laptop = new("\\\\?\\USB#integrated", "Integrated Camera");
    private static readonly CameraDevice Pi = new(MjpegCameraSource.DeviceId, MjpegCameraSource.DeviceName);

    private sealed class ThrowingList : ICameraSource
    {
        public Task<IReadOnlyList<CameraDevice>> ListAsync() => throw new CameraException("웹캠 권한 없음");
        public Task StartAsync(string deviceId, CancellationToken ct = default) => throw new CameraException("x");
        public Task StopAsync() => Task.CompletedTask;
        public bool IsRunning => false;
        public event Action<CameraFrame>? FrameArrived { add { } remove { } }
        public void Dispose() { }
    }

    [Fact]
    public async Task List_MergesLocalThenNetwork()
    {
        var local = new FakeCameraSource { List = { Laptop } };
        var net = new FakeCameraSource { List = { Pi } };
        var c = new CompositeCameraSource(local, net);
        var list = await c.ListAsync();
        Assert.Equal(new[] { Laptop, Pi }, list);
    }

    [Fact]
    public async Task List_KeepsNetwork_WhenLocalFails()
    {
        var c = new CompositeCameraSource(new ThrowingList(), new FakeCameraSource { List = { Pi } });
        var list = await c.ListAsync();
        Assert.Equal(new[] { Pi }, list);
    }

    [Fact]
    public async Task Start_RoutesById_AndStopsOther()
    {
        var local = new FakeCameraSource { List = { Laptop } };
        var net = new FakeCameraSource { List = { Pi } };
        var c = new CompositeCameraSource(local, net);
        var frames = new List<int>();
        c.FrameArrived += f => frames.Add(f.Width);

        await c.StartAsync(Laptop.Id);
        Assert.True(c.IsRunning);
        local.Push(10, 10); net.Push(20, 20);           // 활성(local)만 통과
        Assert.Equal(new[] { 10 }, frames);

        await c.StartAsync(Pi.Id);
        Assert.Equal(1, local.StopCount);
        Assert.Equal(new[] { Pi.Id }, net.Started);
        local.Push(10, 10); net.Push(20, 20);
        Assert.Equal(new[] { 10, 20 }, frames);

        await c.StopAsync();
        Assert.False(c.IsRunning);
        Assert.Equal(1, net.StopCount);
    }

    [Fact]
    public async Task ViewModel_ShowsBoth_DefaultsToLaptop_CanSwitchToPi()
    {
        var local = new FakeCameraSource { List = { Laptop } };
        var net = new FakeCameraSource { List = { Pi } };
        var settings = AppSettings.InMemory();
        var vm = new CameraViewModel(new CompositeCameraSource(local, net), settings);
        await vm.InitializeAsync();

        Assert.Equal(new[] { Laptop, Pi, CameraViewModel.Off }, vm.Devices);
        Assert.Equal(Laptop, vm.SelectedDevice);
        Assert.False(vm.IsNetworkSelected);

        vm.SelectedDevice = Pi;
        await Task.Delay(50);
        Assert.True(vm.IsNetworkSelected);
        Assert.Equal(new[] { Pi.Id }, net.Started);
        Assert.Equal(1, local.StopCount);
        Assert.Equal(MjpegCameraSource.DeviceId, settings.CameraDeviceId);
        Assert.True(vm.IsRunning);
    }

    [Fact]
    public async Task ViewModel_SavedPiSelection_IsRestored_AndUrlChangeRestarts()
    {
        var local = new FakeCameraSource { List = { Laptop } };
        var net = new FakeCameraSource { List = { Pi } };
        var settings = AppSettings.InMemory();
        settings.CameraDeviceId = MjpegCameraSource.DeviceId;
        var vm = new CameraViewModel(new CompositeCameraSource(local, net), settings);
        await vm.InitializeAsync();
        Assert.Equal(Pi, vm.SelectedDevice);
        Assert.Equal(new[] { Pi.Id }, net.Started);

        vm.PiCameraUrl = "http://10.0.0.5:8889/stream.mjpg";
        await Task.Delay(50);
        Assert.Equal("http://10.0.0.5:8889/stream.mjpg", settings.PiCameraUrl);
        Assert.Equal(2, net.Started.Count);                  // URL 이 바뀌면 다시 붙음
    }

    [Fact]
    public void ResolvePiCameraUrl_DerivesFromHost_UnlessOverridden()
    {
        var s = AppSettings.InMemory();
        Assert.Equal("http://192.168.10.2:8889/stream.mjpg", s.ResolvePiCameraUrl("192.168.10.2"));
        s.PiCameraHost = "172.30.1.81";
        Assert.Equal("http://172.30.1.81:8889/stream.mjpg", s.ResolvePiCameraUrl("192.168.10.2"));
        Assert.Equal("172.30.1.81", s.ResolvePiNoteHost("192.168.10.2"));
        s.PiCameraUrl = "http://pi.local:9000/x.mjpg";
        Assert.Equal("http://pi.local:9000/x.mjpg", s.ResolvePiCameraUrl("192.168.10.2"));
        Assert.Equal("pi.local", s.ResolvePiNoteHost("192.168.10.2"));
    }
}
