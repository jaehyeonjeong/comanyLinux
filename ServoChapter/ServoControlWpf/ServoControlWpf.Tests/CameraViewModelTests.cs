using ServoControlWpf.Model;
using ServoControlWpf.Services;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>장치 목록·시작·정지·오류를 기록하는 가짜 카메라. 프레임도 직접 밀어 넣을 수 있다.</summary>
internal sealed class FakeCameraSource : ICameraSource
{
    public List<CameraDevice> List { get; } = new();
    public List<string> Started { get; } = new();
    public int StopCount { get; private set; }
    public bool FailStart { get; set; }
    public bool IsRunning { get; private set; }
    public event Action<CameraFrame>? FrameArrived;

    public Task<IReadOnlyList<CameraDevice>> ListAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>(List);
    public Task StartAsync(string deviceId, CancellationToken ct = default)
    {
        if (FailStart) throw new CameraException("가짜 카메라 열기 실패");
        Started.Add(deviceId);
        IsRunning = true;
        return Task.CompletedTask;
    }
    public Task StopAsync() { if (IsRunning) StopCount++; IsRunning = false; return Task.CompletedTask; }
    public void Push(int w, int h) => FrameArrived?.Invoke(new CameraFrame(w, h, new byte[w * h * 4]));
    public void Dispose() { }
}

public class CameraViewModelTests
{
    private static readonly CameraDevice Laptop = new("\\\\?\\USB#VID_1#integrated", "Integrated Camera");
    private static readonly CameraDevice Usb = new("\\\\?\\USB#VID_2#usb", "USB Camera");

    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public async Task Initialize_PicksFirstDevice_AndStarts()
    {
        var cam = new FakeCameraSource { List = { Laptop, Usb } };
        var vm = new CameraViewModel(cam, AppSettings.InMemory());
        await vm.InitializeAsync();

        Assert.Equal(3, vm.Devices.Count);                       // 장치 2 + 꺼짐
        Assert.Equal(Laptop, vm.SelectedDevice);                 // 기본 = 첫 번째(노트북)
        Assert.Equal(new[] { Laptop.Id }, cam.Started);
        Assert.True(vm.IsRunning);
        Assert.Contains("영상 수신 중", vm.StatusText);
        Assert.Equal("", vm.OverlayText);
    }

    [Fact]
    public async Task Initialize_UsesSavedDevice_WhenPresent()
    {
        var cam = new FakeCameraSource { List = { Laptop, Usb } };
        var settings = AppSettings.InMemory(); settings.CameraDeviceId = Usb.Id;
        var vm = new CameraViewModel(cam, settings);
        await vm.InitializeAsync();
        Assert.Equal(Usb, vm.SelectedDevice);
        Assert.Equal(new[] { Usb.Id }, cam.Started);
    }

    [Fact]
    public async Task NoDevices_ShowsHint_AndDoesNotStart()
    {
        var cam = new FakeCameraSource();
        var vm = new CameraViewModel(cam, AppSettings.InMemory());
        await vm.InitializeAsync();
        Assert.Single(vm.Devices);                               // 꺼짐만
        Assert.Equal(CameraViewModel.Off, vm.SelectedDevice);
        Assert.Empty(cam.Started);
        Assert.False(vm.IsRunning);
        Assert.Contains("연결된 카메라가 없습니다", vm.OverlayText);
    }

    [Fact]
    public async Task SelectingOff_Stops_AndSavesSetting()
    {
        var cam = new FakeCameraSource { List = { Laptop } };
        var settings = AppSettings.InMemory();
        var vm = new CameraViewModel(cam, settings);
        await vm.InitializeAsync();
        Assert.True(vm.IsRunning);

        vm.SelectedDevice = CameraViewModel.Off;
        await Task.Delay(50);
        Assert.False(vm.IsRunning);
        Assert.Equal(1, cam.StopCount);
        Assert.False(settings.CameraEnabled);
        Assert.Equal("", settings.CameraDeviceId);
    }

    [Fact]
    public async Task NullSelection_FromClosingWindow_DoesNotDisableCamera()
    {
        var cam = new FakeCameraSource { List = { Laptop } };
        var settings = AppSettings.InMemory();
        var vm = new CameraViewModel(cam, settings);
        await vm.InitializeAsync();
        vm.SelectedDevice = null;                                // WPF 가 창을 닫으며 SelectedItem 을 null 로
        Assert.True(settings.CameraEnabled);                     // 다음 실행 때 카메라가 꺼져 있으면 안 됨
        Assert.Equal(Laptop, vm.SelectedDevice);
    }

    [Fact]
    public async Task StartFailure_ShowsMessage_KeepsAppAlive()
    {
        var cam = new FakeCameraSource { List = { Laptop }, FailStart = true };
        var vm = new CameraViewModel(cam, AppSettings.InMemory());
        await vm.InitializeAsync();
        Assert.False(vm.IsRunning);
        Assert.Contains("카메라 오류", vm.StatusText);
        Assert.Contains("가짜 카메라 열기 실패", vm.OverlayText);
    }

    [Fact]
    public void Frames_AreRenderedIntoBitmap_OnUiContext()
    {
        OnSta(() =>
        {
            // UI 스레드 흉내: 동기 SynchronizationContext 로 Post 가 즉시 실행되게 함
            SynchronizationContext.SetSynchronizationContext(new ImmediateContext());
            var cam = new FakeCameraSource { List = { Laptop } };
            var vm = new CameraViewModel(cam, AppSettings.InMemory());
            vm.InitializeAsync().GetAwaiter().GetResult();

            cam.Push(64, 48);
            Assert.NotNull(vm.Frame);
            Assert.Equal(64, vm.FrameWidth);
            Assert.Equal(48, vm.FrameHeight);
            var first = vm.Frame;
            cam.Push(64, 48);
            Assert.Same(first, vm.Frame);                        // 같은 크기면 같은 비트맵을 덮어씀
            cam.Push(32, 24);
            Assert.NotSame(first, vm.Frame);                     // 크기가 바뀌면 새 비트맵
            return 0;
        });
    }

    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
