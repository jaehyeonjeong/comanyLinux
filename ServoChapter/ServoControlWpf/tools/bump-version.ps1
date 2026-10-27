<#
.SYNOPSIS
  버전 올리기: VERSION 파일 갱신 + CHANGELOG.md 의 [Unreleased] 를 새 버전 절로 승격.

.EXAMPLE
  .\tools\bump-version.ps1 -Part patch          # 1.1.0 → 1.1.1
  .\tools\bump-version.ps1 -Part minor          # 1.1.0 → 1.2.0
  .\tools\bump-version.ps1 -Set 2.0.0-rc1
  .\tools\bump-version.ps1 -Part minor -DryRun  # 바뀔 내용만 보여 줌

.NOTES
  - VERSION 은 UTF-8(BOM 없음) 한 줄로 쓴다 (MSBuild·Python 이 그대로 읽음).
  - CHANGELOG 의 "## [Unreleased]" 아래에 적힌 내용이 없으면 멈춘다 (-Force 로 우회).
  - 두 파일을 모두 메모리에서 만든 뒤 한꺼번에 쓴다 (절반만 바뀌는 상태 방지).
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')] [string] $Part,
    [string] $Set,
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd'),
    [switch] $DryRun,
    [switch] $Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root 'VERSION'
$changelog = Join-Path $root 'CHANGELOG.md'
$semver = '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'

if (-not $Part -and -not $Set) { throw "-Part major|minor|patch 또는 -Set x.y.z 를 주세요." }
if ($Part -and $Set) { throw "-Part 와 -Set 은 같이 쓸 수 없습니다." }

$current = (Get-Content $versionFile -Raw -Encoding UTF8).Trim()
if ($current -notmatch $semver) { throw "VERSION 파일의 값이 SemVer 가 아닙니다: '$current'" }

if ($Set) {
    if ($Set -notmatch $semver) { throw "새 버전이 SemVer 가 아닙니다: '$Set'" }
    $new = $Set
} else {
    $m = [regex]::Match($current, $semver)
    $maj = [int]$m.Groups[1].Value; $min = [int]$m.Groups[2].Value; $pat = [int]$m.Groups[3].Value
    switch ($Part) {
        'major' { $maj++; $min = 0; $pat = 0 }
        'minor' { $min++; $pat = 0 }
        'patch' { $pat++ }
    }
    $new = "$maj.$min.$pat"
}

function Compare-Semver([string] $a, [string] $b) {
    $pa = [regex]::Match($a, $semver); $pb = [regex]::Match($b, $semver)
    for ($i = 1; $i -le 3; $i++) {
        $d = [int]$pa.Groups[$i].Value - [int]$pb.Groups[$i].Value
        if ($d -ne 0) { return $d }
    }
    # 같은 숫자면 프리릴리스가 없는 쪽이 더 큼
    $preA = $pa.Groups[4].Value; $preB = $pb.Groups[4].Value
    if ($preA -eq $preB) { return 0 }
    if ($preA -eq '') { return 1 }
    if ($preB -eq '') { return -1 }
    return [string]::CompareOrdinal($preA, $preB)
}
if ((Compare-Semver $new $current) -le 0) { throw "새 버전 $new 이(가) 현재 $current 보다 크지 않습니다." }

# CHANGELOG: [Unreleased] 절을 찾아 내용 확인 후 승격
$text = Get-Content $changelog -Raw -Encoding UTF8
$unrel = [regex]::Match($text, '(?ms)^## \[Unreleased\]\r?\n(.*?)(?=^## \[|\z)')
if (-not $unrel.Success) { throw "CHANGELOG.md 에 '## [Unreleased]' 절이 없습니다." }
$body = $unrel.Groups[1].Value
$hasContent = ($body -split "`r?`n" | Where-Object { $_ -match '^\s*[-*]\s+\S' }).Count -gt 0
if (-not $hasContent -and -not $Force) {
    throw "CHANGELOG.md 의 [Unreleased] 에 적힌 변경 내용이 없습니다. 먼저 적거나 -Force 를 쓰세요."
}
$nl = if ($text -match "`r`n") { "`r`n" } else { "`n" }
$replacement = "## [Unreleased]$nl$nl## [$new] - $Date$nl" + $body
$newText = $text.Substring(0, $unrel.Index) + $replacement + $text.Substring($unrel.Index + $unrel.Length)

Write-Host "VERSION   : $current → $new"
Write-Host "CHANGELOG : [Unreleased] → [$new] - $Date  ($(($body -split "`r?`n").Count) 줄 승격)"
if ($DryRun) { Write-Host "(dry run — 파일은 바꾸지 않음)"; exit 0 }

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($changelog, $newText, $utf8)
[System.IO.File]::WriteAllText($versionFile, "$new`n", $utf8)

Write-Host ""
Write-Host "완료. 다음 단계:"
Write-Host "  dotnet build ServoControlWpf.sln -c Release      # 제목 표시줄에 v$new"
Write-Host "  dotnet test  ServoControlWpf.sln -c Release      # VersionTests 가 VERSION·CHANGELOG 일치를 검사"
Write-Host "  ./pi_display/deploy_pi_display.sh rpi            # Pi 프로그램도 같은 VERSION 을 받음"
if (Test-Path (Join-Path $root '.git')) { Write-Host "  git commit -am 'v$new' && git tag v$new" }
