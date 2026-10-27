using ServoControlWpf.Model;
using ServoControlWpf.ViewModel;
using Xunit;

namespace ServoControlWpf.Tests;

internal sealed class FakeDialogs : IDialogService
{
    public List<string> Errors { get; } = new();
    public Action? OnError { get; set; }
    public void ShowError(string text) { OnError?.Invoke(); Errors.Add(text); }
    public bool Confirm(string title, string text) => true;
    public string? PickFolder(string title, string? initialDir) => null;
}

/// <summary>각도 ↔ 펄스폭 양방향 조작이 ViewModel 에서 서로를 갱신하고 드라이버로 출력되는지</summary>
public class MainViewModelTests
{
    private static (MainViewModel vm, RecordingDriver drv, FakeDialogs dlg) Make(ServoSpec? spec = null)
    {
        var drv = new RecordingDriver();
        var model = new ServoModel(drv);
        model.Start(spec ?? ServoSpec.Hs311Official());
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(model, AppSettings.InMemory(), dlg);   // 테스트가 실제 설정 파일을 건드리지 않게
        return (vm, drv, dlg);
    }

    [Fact]
    public void SettingAngle_UpdatesPulse_AndWrites()
    {
        var (vm, drv, _) = Make();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Angle = 20;

        Assert.Equal(1700, vm.Pulse);
        Assert.Equal(1700, vm.PulseInput);
        Assert.Equal(new[] { 1700 }, drv.Pulses);
        Assert.Contains(nameof(MainViewModel.PulseInput), changed);
        Assert.True(vm.IsOutputOn);
    }

    [Fact]
    public void SettingPulse_UpdatesAngle_AndWrites()
    {
        var (vm, drv, _) = Make();

        vm.PulseInput = 1300;            // −20°

        Assert.Equal(-20.0, vm.Angle, 6);
        Assert.Equal(1300, vm.Pulse);
        Assert.Equal(new[] { 1300 }, drv.Pulses);
        Assert.Contains("1300 µs", vm.OutputText);
    }

    [Fact]
    public void SettingPulse_OutsideRange_IsClamped()
    {
        var (vm, drv, _) = Make();

        vm.PulseInput = 2400;            // 공식 범위 최대 1900 으로 잘림

        Assert.Equal(40.0, vm.Angle, 6);
        Assert.Equal(1900, drv.Pulses[^1]);
        Assert.Equal(1100, vm.MinPulse);
        Assert.Equal(1900, vm.MaxPulse);
    }

    [Fact]
    public void Pulse_RoundTrip_WithAsymmetricCalibration()
    {
        // −쪽 400 µs/40°, +쪽 300 µs/40° : 1655 µs → 20.67° → 1655 µs
        var (vm, drv, _) = Make(new ServoSpec(1100, 1500, 1800, -40, 40, 18));

        vm.PulseInput = 1655;

        Assert.Equal(1655, vm.Pulse);
        Assert.Equal(20.667, vm.Angle, 2);
        vm.Angle = -10;
        Assert.Equal(1400, vm.PulseInput);
    }

    [Fact]
    public void SettingPulse_WhenDriverFails_RevertsAndReportsError()
    {
        var (vm, drv, dlg) = Make();
        vm.Angle = 10;
        drv.FailNextWrite = true;

        vm.PulseInput = 1800;

        Assert.Equal(10.0, vm.Angle, 6);
        Assert.Equal(1600, vm.PulseInput);
        Assert.Single(dlg.Errors);
    }
}
