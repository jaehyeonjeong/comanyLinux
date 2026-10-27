using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>
/// MainWindow.xaml 의 핵심 바인딩이 빠지지 않았는지 검사.
/// 카메라 뷰의 IsConnected 바인딩이 편집 중에 사라져 "연결해도 카메라가 연결 안 됨으로 보이는" 문제가 있었다 — 재발 방지.
/// (XAML 은 테스트 출력 폴더로 복사된 원본 파일을 읽는다)
/// </summary>
public class MainWindowXamlTests
{
    private static string Xaml => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));

    private static string CameraElement()
    {
        var m = Regex.Match(Xaml, @"<v:ServoCameraView\b[^>]*>", RegexOptions.Singleline);
        Assert.True(m.Success, "MainWindow.xaml 에 v:ServoCameraView 요소가 있어야 함");
        return m.Value;
    }

    [Theory]
    [InlineData("Angle")]
    [InlineData("IsOutputOn")]
    [InlineData("IsConnected")]
    [InlineData("Pulse")]
    [InlineData("MinAngle")]
    [InlineData("MaxAngle")]
    public void CameraView_BindsProperty(string property)
    {
        var el = CameraElement();
        Assert.Matches(property + @"=""\{Binding " + property + @"\b", el);
    }

    [Fact]
    public void CameraView_IsDisplayOnly()
    {
        Assert.Contains("IsHitTestVisible=\"False\"", CameraElement());
    }

    [Theory]
    [InlineData("KeepOutputOnDisconnect")]
    [InlineData("PulseInput")]
    [InlineData("ConnectToggleCommand")]
    [InlineData("Title")]
    [InlineData("VersionText")]
    [InlineData("PiCameraUrl")]
    [InlineData("IsNetworkSelected")]
    [InlineData("RunDeployPiDisplayCommand")]
    [InlineData("OpenDeployPiDisplayTerminalCommand")]
    [InlineData("EmulatorKindIndex")]
    [InlineData("RecordCommand")]
    [InlineData("RecordText")]
    [InlineData("SnapshotCommand")]
    [InlineData("IsDetectOn")]
    [InlineData("IsPiPanelVisible")]
    [InlineData("RecordMode")]
    [InlineData("Hflip")]
    [InlineData("Vflip")]
    [InlineData("Brightness")]
    [InlineData("Awb")]
    [InlineData("Wsl.RecordingsDir")]
    [InlineData("Wsl.PullRecordingsCommand")]
    [InlineData("Wsl.InstallWatchCommand")]
    [InlineData("Wsl.WatchStatusCommand")]
    [InlineData("Wsl.LastPullSummary")]
    [InlineData("AutoPullRecordings")]
    [InlineData("OpenRecordingsFolderCommand")]
    [InlineData("CalUsPerDeg")]
    [InlineData("CalMinAngle")]
    [InlineData("CalMaxPulse")]
    public void Window_BindsViewModelMember(string member)
    {
        Assert.Contains("{Binding " + member, Xaml);
    }

    /// <summary>길이가 들쭉날쭉한 문구는 잘리지 않게 ShrinkTextBlock(자동 축소 + … + 툴팁)으로 표시한다</summary>
    [Theory]
    [InlineData("ConnInfoText")]
    [InlineData("EmulatorText")]
    [InlineData("DriverName")]
    [InlineData("StatusText")]
    [InlineData("OutputText")]
    [InlineData("RecordInfoText")]
    [InlineData("DetectText")]
    [InlineData("PiSettingsInfo")]
    [InlineData("CalibText")]
    [InlineData("CalLinkText")]
    public void LongText_UsesShrinkTextBlock(string member)
    {
        Assert.Matches(@"<v:ShrinkTextBlock[^>]*\{Binding " + member + @"\b", Xaml);
    }

    /// <summary>UI 자동화 검사가 찾을 수 있게 녹화·사진·인식·녹화 모드 컨트롤에 접근성 이름이 있어야 한다</summary>
    [Theory]
    [InlineData("녹화")]
    [InlineData("사진")]
    [InlineData("인식")]
    [InlineData("녹화 모드")]
    [InlineData("밝기")]
    public void PiControls_HaveAutomationNames(string name)
    {
        Assert.Contains($"AutomationProperties.Name=\"{name}\"", Xaml);
    }

    /// <summary>카메라 설정은 영상 영역을 줄이지 않게 오른쪽 "Pi 카메라" 탭에 둔다 (카메라 패널 안에 Expander 로 두지 않음)</summary>
    [Fact]
    public void PiCameraSettings_LiveInRightTab_NotInCameraPanel()
    {
        Assert.Contains("TabItem Header=\"  Pi 카메라  \"", Xaml);
        var cameraPanel = Regex.Match(Xaml, @"<GroupBox[^>]*Header=""카메라 화면"".*?</GroupBox>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(cameraPanel);
        Assert.DoesNotContain("<Expander", cameraPanel);
        Assert.DoesNotContain("{Binding Brightness", cameraPanel);
        Assert.Contains("{Binding RecordCommand", cameraPanel);
    }
}
