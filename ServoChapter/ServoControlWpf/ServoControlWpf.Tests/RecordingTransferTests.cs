using System.IO;
using System.Text.Json;
using ServoControlWpf.Model;
using ServoControlWpf.Services;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>녹화 전송 설정(AppSettings)·WSL 뷰모델의 가져오기 명령·MainViewModel 의 자동 전송 연결</summary>
public class RecordingTransferTests
{
    [Fact]
    public void Settings_Defaults_AndRoundTrip()
    {
        var s = AppSettings.InMemory();
        Assert.Equal("~/MyJob/projects/comanyLinux/ServoChapter/recordings", s.RecordingsDir);
        Assert.True(s.AutoPullRecordings);
        Assert.Equal("http://192.168.10.2:8889", s.ResolvePiApiBase("192.168.10.2"));
        Assert.Equal("http://127.0.0.1:8889", s.ResolvePiApiBase(null));
        s.PiCameraUrl = "http://172.30.1.81:8889/stream.mjpg";
        Assert.Equal("http://172.30.1.81:8889", s.ResolvePiApiBase("ignored"));

        s.RecordingsDir = "~/Videos/x";
        s.AutoPullRecordings = false;
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("~/Videos/x", back.RecordingsDir);
        Assert.False(back.AutoPullRecordings);
    }

    [Theory]
    [InlineData(new[] { "받음: a.avi", "PULLED 2" }, 2)]
    [InlineData(new[] { "PULLED 0" }, 0)]
    [InlineData(new[] { "  PULLED 7  ", "뒤에 다른 줄" }, 7)]
    [InlineData(new[] { "rsync 오류" }, -1)]
    [InlineData(new string[0], -1)]
    public void ParsePulled_FindsLastCount(string[] lines, int expected)
    {
        Assert.Equal(expected, WslViewModel.ParsePulled(lines));
    }

    [Fact]
    public void WslViewModel_HasTransferCommands_AndRecordingsDirSaves()
    {
        var settings = AppSettings.InMemory();
        var vm = new WslViewModel(settings, new FakeDialogs());
        Assert.Equal(AppSettings.DefaultRecordingsDir, vm.RecordingsDir);
        Assert.True(vm.PullRecordingsCommand.CanExecute(null));
        Assert.True(vm.InstallWatchCommand.CanExecute(null));
        Assert.True(vm.WatchStatusCommand.CanExecute(null));
        vm.RecordingsDir = "  ~/rec  ";
        Assert.Equal("~/rec", settings.RecordingsDir);
        vm.RecordingsDir = "";
        Assert.Equal(AppSettings.DefaultRecordingsDir, vm.RecordingsDir);     // 비우면 기본값
        Assert.EndsWith("pi_display", vm.PiDisplayDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(vm.PiDisplayDir, "pull_recordings.sh")));
        Assert.True(File.Exists(Path.Combine(vm.PiDisplayDir, "watch_recordings.sh")));
    }

    [Fact]
    public void MainViewModel_ExposesAutoPull_AndFolderCommand()
    {
        var drv = new RecordingDriver();
        var model = new ServoModel(drv);
        model.Start(ServoSpec.Hs311Official());
        var settings = AppSettings.InMemory();
        var vm = new MainViewModel(model, settings, new FakeDialogs(), new FakeCameraSource(), new PiCameraApi(() => "http://127.0.0.1:1/stream.mjpg"));
        Assert.True(vm.AutoPullRecordings);
        vm.AutoPullRecordings = false;
        Assert.False(settings.AutoPullRecordings);
        Assert.NotNull(vm.OpenRecordingsFolderCommand);
        Assert.Equal("", vm.LastTransferText);
    }

    [Fact]
    public async Task MainViewModel_AutoPullOff_OnlyNotes()
    {
        var drv = new RecordingDriver();
        var model = new ServoModel(drv);
        model.Start(ServoSpec.Hs311Official());
        var settings = AppSettings.InMemory();
        settings.AutoPullRecordings = false;
        settings.CameraDeviceId = MjpegCameraSource.DeviceId;
        using var srv = new FakePiApiServer();
        var cam = new FakeCameraSource { List = { new CameraDevice(MjpegCameraSource.DeviceId, MjpegCameraSource.DeviceName) } };
        var vm = new MainViewModel(model, settings, new FakeDialogs(), cam, new PiCameraApi(() => srv.StreamUrl));
        vm.Camera.PollIntervalMs = 1000;
        await vm.Camera.InitializeAsync();
        vm.Camera.SnapshotCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!vm.Camera.RecordInfoText.Contains("자동 가져오기") && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Contains("자동 가져오기가 꺼져", vm.Camera.RecordInfoText);
        Assert.Contains("img_20261027_120000.jpg", vm.Camera.RecordInfoText);
        vm.Camera.Dispose();
    }
}
