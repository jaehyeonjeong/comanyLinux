using System.IO;
using System.Text.RegularExpressions;
using ServoControlWpf.Services;
using Xunit;

namespace ServoControlWpf.Tests;

/// <summary>버전의 단일 진실(VERSION 파일)이 어셈블리 버전·CHANGELOG 와 맞는지</summary>
public class VersionTests
{
    private static string VersionFile => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "VERSION")).Trim();

    [Fact]
    public void VersionFile_IsSemVer_AndMatchesAssembly()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", VersionFile);
        Assert.Equal(VersionFile, AppVersion.Short);       // Directory.Build.props 가 VERSION 을 읽어 박아 넣음
    }

    [Fact]
    public void Changelog_HasSectionForCurrentVersion()
    {
        var changelog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"));
        Assert.Contains("## [Unreleased]", changelog);
        Assert.Contains($"## [{VersionFile}]", changelog);
    }
}
