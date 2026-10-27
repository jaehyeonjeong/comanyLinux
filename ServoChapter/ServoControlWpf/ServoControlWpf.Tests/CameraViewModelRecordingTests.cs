using System.Text.Json;
using ServoControlWpf.Model;
using ServoControlWpf.Services;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// 카메라 패널의 Pi 조작(녹화·사진·설정·인식)이 가짜 pi_display API 서버와 맞물리는지.
/// 네트워크 카메라("mjpeg:pi")를 골라 영상 수신 상태가 되어야 버튼이 살고, 상태 폴링이 돈다.
/// </summary>
public class CameraViewModelRecordingTests
{
    private static readonly CameraDevice Pi = new(MjpegCameraSource.DeviceId, MjpegCameraSource.DeviceName);
    private static readonly CameraDevice Laptop = new("\\\\?\\USB#VID_1#integrated", "Integrated Camera");

    private static async Task<(CameraViewModel vm, FakePiApiServer srv, FakeCameraSource cam)> MakeNetworkAsync(bool pollFast = true)
    {
        var srv = new FakePiApiServer();
        var cam = new FakeCameraSource { List = { Laptop, Pi } };
        var settings = AppSettings.InMemory();
        settings.CameraDeviceId = Pi.Id;
        var api = new PiCameraApi(() => srv.StreamUrl);
        var vm = new CameraViewModel(cam, settings, api) { SettingsDebounceMs = 30, PollIntervalMs = pollFast ? 50 : 1000 };
        await vm.InitializeAsync();
        return (vm, srv, cam);
    }

    private static async Task WaitAsync(Func<bool> pred, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!pred() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(pred());
    }

    [Fact]
    public async Task WithoutApi_PanelHidden_AndCommandsDisabled()
    {
        var cam = new FakeCameraSource { List = { Pi } };
        var settings = AppSettings.InMemory(); settings.CameraDeviceId = Pi.Id;
        var vm = new CameraViewModel(cam, settings);
        await vm.InitializeAsync();
        Assert.True(vm.IsNetworkSelected);
        Assert.False(vm.IsPiPanelVisible);
        Assert.False(vm.CanControlPi);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.Equal("● 녹화", vm.RecordText);
    }

    [Fact]
    public async Task NetworkCamera_EnablesPanel_LoadsSettingsFromStatus()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            Assert.True(vm.IsPiPanelVisible);
            Assert.True(vm.CanControlPi);
            Assert.True(vm.RecordCommand.CanExecute(null));
            Assert.True(vm.SnapshotCommand.CanExecute(null));
            // 첫 status.json 에서 서버 설정을 읽어 옴 (720p60, hflip, brightness 0.25, awb daylight)
            await WaitAsync(() => vm.RecordMode == "720p60");
            Assert.True(vm.Hflip);
            Assert.Equal(0.25, vm.Brightness);
            Assert.Equal("daylight", vm.Awb);
            Assert.Contains("미리보기 크기", vm.PiSettingsInfo);          // 가짜 서버는 record_capable = mjpeg-avi
            Assert.Contains("720p60", vm.PiSettingsInfo);
            Assert.Contains("인식 꺼짐 (fake)", vm.DetectText);           // 서버는 꺼짐 상태로 시작
            Assert.False(vm.IsDetectOn);
            // 서버 값을 화면에 넣는 것은 다시 서버로 보내지 않는다
            await Task.Delay(120);
            Assert.DoesNotContain(srv.Snapshot(), r => r.method == "POST" && r.path == "/camera/settings");
        }
    }

    [Fact]
    public async Task LaptopCamera_PanelHidden()
    {
        var srv = new FakePiApiServer();
        var cam = new FakeCameraSource { List = { Laptop, Pi } };
        var vm = new CameraViewModel(cam, AppSettings.InMemory(), new PiCameraApi(() => srv.StreamUrl));
        using (srv) using (vm)
        {
            await vm.InitializeAsync();
            Assert.Equal(Laptop, vm.SelectedDevice);
            Assert.False(vm.IsPiPanelVisible);
            Assert.False(vm.CanControlPi);
            await Task.Delay(100);
            Assert.Empty(srv.Snapshot());                       // 노트북 카메라일 때는 Pi 에 아무것도 묻지 않음
        }
    }

    [Fact]
    public async Task RecordToggle_StartsStops_AndRaisesPiFileSaved()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            var saved = new List<string>();
            vm.PiFileSaved += saved.Add;

            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.RecordInfoText.StartsWith("녹화 중"));       // 시작 응답 처리 끝 (폴링이 먼저 알아챌 수도 있음)
            Assert.True(vm.IsRecording);
            Assert.StartsWith("■ 정지", vm.RecordText);
            Assert.Contains("rec_x.avi", vm.RecordInfoText);
            Assert.Contains("MJPEG → AVI", vm.RecordInfoText);
            await WaitAsync(() => vm.RecordText.Contains("00:00:05"));          // 폴링이 시간·용량을 채움
            Assert.Contains("121 KB", vm.RecordText);

            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.RecordInfoText.StartsWith("녹화 저장"));     // 정지 응답 처리 끝
            Assert.False(vm.IsRecording);
            Assert.Equal("● 녹화", vm.RecordText);
            Assert.Contains("녹화 저장: rec_x.avi", vm.RecordInfoText);
            Assert.Contains("2.0 MB", vm.RecordInfoText);
            Assert.Contains("00:00:05", vm.RecordInfoText);
            Assert.Equal(new[] { "rec_x.avi" }, saved);
            Assert.Equal(1, srv.Snapshot().Count(r => r.path == "/record/stop"));
        }
    }

    [Fact]
    public async Task RecordingEndedOnPiSide_IsNoticedByPolling()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            var saved = new List<string>();
            vm.PiFileSaved += saved.Add;
            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.IsRecording);
            // Pi 가 스스로 멈춤 (시간 제한) → status.json 에 recording:false + last
            srv.Recording = false;
            srv.Routes["/status.json"] = (200, """
                {"version":"9.9.9","record":{"recording":false,"kind":"mjpeg-avi","file":null,"seconds":0,"bytes":0,"frames":0,"dir":"/x","max_minutes":1,
                 "last":{"name":"rec_x.avi","bytes":3145728,"seconds":60.0,"frames":600,"kind":"mjpeg-avi","reason":"시간 제한 1분"},"disk":{"free_mb":1,"used_mb":0,"max_gb":8}}}
                """);
            await WaitAsync(() => !vm.IsRecording);
            Assert.Contains("시간 제한", vm.RecordInfoText);
            Assert.Equal(new[] { "rec_x.avi" }, saved);
        }
    }

    [Fact]
    public async Task Snapshot_ShowsResult_AndRaisesPiFileSaved()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            var saved = new List<string>();
            vm.PiFileSaved += saved.Add;
            vm.SnapshotCommand.Execute(null);
            await WaitAsync(() => vm.RecordInfoText.StartsWith("사진 저장"));
            Assert.Contains("2592×1944", vm.RecordInfoText);
            Assert.Contains("1.0 MB", vm.RecordInfoText);
            Assert.Equal(new[] { "img_20261027_120000.jpg" }, saved);
        }
    }

    [Fact]
    public async Task ApiError_ShowsMessage_NoCrash()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            srv.Routes["/record/start"] = (507, """{"error":"디스크 여유 공간이 100 MB 뿐입니다 (최소 500 MB)"}""");
            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.RecordInfoText.StartsWith("녹화 오류"));
            Assert.Contains("디스크 여유 공간", vm.RecordInfoText);
            Assert.False(vm.IsRecording);
            Assert.False(vm.IsPiBusy);
            Assert.True(vm.RecordCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task SettingsChanges_AreDebounced_IntoOneRequest()
    {
        var (vm, srv, _) = await MakeNetworkAsync(pollFast: false);
        using (srv) using (vm)
        {
            await WaitAsync(() => vm.RecordMode == "720p60");            // 서버 값 로드 끝
            var before = srv.Snapshot().Count(r => r.method == "POST");
            vm.Brightness = 0.3;
            vm.Brightness = 0.5;                                         // 연달아 바꿈 → 마지막 값만
            vm.RecordMode = "480p90";
            await WaitAsync(() => srv.Snapshot().Count(r => r.method == "POST" && r.path == "/camera/settings") == 1);
            if (vm.LastSettingsFlush is not null) await vm.LastSettingsFlush;
            var sent = srv.Snapshot().Single(r => r.method == "POST" && r.path == "/camera/settings");
            using var doc = JsonDocument.Parse(sent.body);
            Assert.Equal(0.5, doc.RootElement.GetProperty("brightness").GetDouble());
            Assert.Equal("480p90", doc.RootElement.GetProperty("record_mode").GetString());
            Assert.False(doc.RootElement.TryGetProperty("hflip", out _));          // 안 바꾼 키는 안 보냄
            await WaitAsync(() => vm.RecordInfoText.Contains("480p90"));           // 다시 열었다는 안내
            Assert.Equal(before, srv.Snapshot().Count(r => r.method == "POST") - 1);
        }
    }

    [Fact]
    public async Task DetectCheckbox_SendsOnOff()
    {
        var (vm, srv, _) = await MakeNetworkAsync(pollFast: false);
        using (srv) using (vm)
        {
            await WaitAsync(() => vm.DetectText.Length > 0);
            vm.IsDetectOn = true;
            await WaitAsync(() => srv.Snapshot().Any(r => r.path == "/detect/on"));
            await WaitAsync(() => vm.DetectText.Contains("감지된 것 없음"));
            vm.IsDetectOn = false;
            await WaitAsync(() => srv.Snapshot().Any(r => r.path == "/detect/off"));
            await WaitAsync(() => vm.DetectText.Contains("인식 꺼짐"));
        }
    }

    [Fact]
    public async Task SwitchingToLaptop_StopsPolling_AndClearsRecordingState()
    {
        var (vm, srv, cam) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.IsRecording);
            vm.SelectedDevice = Laptop;
            await WaitAsync(() => !vm.IsPiPanelVisible);
            Assert.False(vm.IsRecording);
            Assert.False(vm.CanControlPi);
            var n = srv.Snapshot().Length;
            await Task.Delay(200);
            Assert.Equal(n, srv.Snapshot().Length);                     // 폴링이 멈췄음
            Assert.Equal(new[] { Pi.Id, Laptop.Id }, cam.Started);
        }
    }

    [Fact]
    public async Task Calibration_IsPushedToPi_WhenItDiffers_AndReportedWhenSame()
    {
        // Pi 화면 프로그램이 공식값(±40°)을 쓰고 WPF 는 600/1500/2400 ±80° 면, 같은 펄스에 다른 각도가 찍힌다 → WPF 값으로 맞춤
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            vm.CalibProvider = () => "600,1500,2400,-80,80";
            await WaitAsync(() => srv.Snapshot().Any(r => r.method == "POST" && r.path == "/servo/calib"));
            var sent = srv.Snapshot().First(r => r.path == "/servo/calib");
            using (var doc = JsonDocument.Parse(sent.body))
                Assert.Equal("600,1500,2400,-80,80", doc.RootElement.GetProperty("calib").GetString());
            await WaitAsync(() => vm.CalibText.Contains("WPF 와 같음"));     // 다음 폴링에서 서버 값이 같아짐
            await Task.Delay(150);
            Assert.Equal(1, srv.Snapshot().Count(r => r.path == "/servo/calib"));   // 같으면 다시 보내지 않음
            Assert.Contains("600,1500,2400,-80,80", vm.CalibText);
        }
    }

    [Fact]
    public async Task Calibration_NotPushed_WithoutProvider()
    {
        var (vm, srv, _) = await MakeNetworkAsync();
        using (srv) using (vm)
        {
            await WaitAsync(() => vm.CalibText.Length > 0);
            Assert.Contains("1100,1500,1900,-40,40", vm.CalibText);
            await Task.Delay(150);
            Assert.DoesNotContain(srv.Snapshot(), r => r.path == "/servo/calib");
        }
    }

    [Fact]
    public async Task StopRecordingIfAny_OnShutdown_SendsStop()
    {
        var (vm, srv, _) = await MakeNetworkAsync(pollFast: false);
        using (srv) using (vm)
        {
            vm.RecordCommand.Execute(null);
            await WaitAsync(() => vm.IsRecording);
            await vm.StopRecordingIfAnyAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(srv.Snapshot(), r => r.path == "/record/stop");
            Assert.False(srv.Recording);
        }
    }

    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(83, "00:01:23")]
    [InlineData(3661.9, "01:01:01")]
    public void Hms_Formats(double s, string expected) => Assert.Equal(expected, CameraViewModel.Hms(s));

    [Theory]
    [InlineData(300, "0 KB")]
    [InlineData(123456, "121 KB")]
    [InlineData(12 * 1024 * 1024, "12.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void Mb_Formats(long bytes, string expected) => Assert.Equal(expected, CameraViewModel.Mb(bytes));
}
