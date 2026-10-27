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

    // ───────── 보정 입력칸 연동 (v1.3.2): 각도 ↔ 펄스가 "1° 당 µs" 로 묶여 있다 ─────────

    [Fact]
    public void CalibrationInputs_AngleEdit_UpdatesPulse()
    {
        var (vm, _, _) = Make();                         // 공식값 1100/1500/1900 ±40° → 기울기 10
        Assert.Equal(10.0, vm.CalUsPerDeg);
        vm.CalMaxAngle = 60;
        Assert.Equal(2100, vm.CalMaxPulse);
        vm.CalMinAngle = -80;
        Assert.Equal(700, vm.CalMinPulse);
        Assert.Equal(1500, vm.CalNeutralPulse);         // 중립·반대쪽은 그대로
        Assert.Equal(2100, vm.CalMaxPulse);
        Assert.Contains("-80° = 700 µs", vm.CalLinkText);
        Assert.Contains("+60° = 2100 µs", vm.CalLinkText);
    }

    [Fact]
    public void CalibrationInputs_PulseEdit_UpdatesAngle()
    {
        var (vm, _, _) = Make();
        vm.CalMinPulse = 600;
        Assert.Equal(-90.0, vm.CalMinAngle, 6);
        vm.CalMaxPulse = 2250;
        Assert.Equal(75.0, vm.CalMaxAngle, 6);
        vm.CalMaxPulse = 1655;                           // 소수 각도는 0.1° 단위
        Assert.Equal(15.5, vm.CalMaxAngle, 6);
    }

    [Fact]
    public void CalibrationInputs_NeutralOrSlopeEdit_KeepsAngles_RecomputesPulses()
    {
        var (vm, _, _) = Make();
        vm.CalNeutralPulse = 1400;
        Assert.Equal((-40.0, 40.0), (vm.CalMinAngle, vm.CalMaxAngle));
        Assert.Equal((1000, 1800), (vm.CalMinPulse, vm.CalMaxPulse));
        vm.CalUsPerDeg = 11.25;
        Assert.Equal((950, 1850), (vm.CalMinPulse, vm.CalMaxPulse));
        vm.CalUsPerDeg = 0;                              // 잘못된 기울기는 무시
        Assert.Equal(11.25, vm.CalUsPerDeg);
        vm.CalUsPerDeg = -3;
        Assert.Equal(11.25, vm.CalUsPerDeg);
    }

    [Fact]
    public void CalibrationInputs_ApplyUsesLinkedValues()
    {
        var (vm, _, dlg) = Make();
        vm.CalMaxAngle = 60;                             // → 2100 µs
        vm.CalMinPulse = 600;                            // → −90°
        vm.ApplySpecCommand.Execute(null);
        Assert.Empty(dlg.Errors);
        Assert.Equal((600, 2100), (vm.MinPulse, vm.MaxPulse));
        Assert.Equal((-90.0, 60.0), (vm.MinAngle, vm.MaxAngle));
        Assert.Equal(1500, vm.CalNeutralPulse);
        vm.Angle = 60;
        Assert.Equal(2100, vm.Pulse);
    }

    [Fact]
    public void CalibrationInputs_LoadedAsymmetricSpec_ShownAsIs_SlopeFromPlusSide()
    {
        // 설정에서 읽어 온 비대칭 보정(−쪽 10 µs/°, +쪽 7.5 µs/°)은 그대로 보여 주고 연동 계산으로 덮어쓰지 않는다
        var (vm, _, _) = Make(new ServoSpec(1100, 1500, 1800, -40, 40, 18));
        Assert.Equal((1100, 1500, 1800), (vm.CalMinPulse, vm.CalNeutralPulse, vm.CalMaxPulse));
        Assert.Equal((-40.0, 40.0), (vm.CalMinAngle, vm.CalMaxAngle));
        Assert.Equal(7.5, vm.CalUsPerDeg);
        vm.CalMinAngle = -20;                            // 그 뒤의 편집은 표시된 기울기(7.5)로 연동
        Assert.Equal(1350, vm.CalMinPulse);
    }

    [Fact]
    public void CalibrationInputs_PresetButtons_ResetSlope()
    {
        var (vm, _, _) = Make();
        vm.CalUsPerDeg = 12;
        vm.ApplyOfficialCommand.Execute(null);
        Assert.Equal(10.0, vm.CalUsPerDeg);
        Assert.Equal((1100, 1900), (vm.CalMinPulse, vm.CalMaxPulse));
        vm.CalMaxAngle = 30;
        Assert.Equal(1800, vm.CalMaxPulse);
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
