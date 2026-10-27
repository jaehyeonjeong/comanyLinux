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
    public void Window_BindsViewModelMember(string member)
    {
        Assert.Contains("{Binding " + member, Xaml);
    }
}
