param(
    [switch]$SkipPublish,
    [switch]$SkipTests,
    [string]$IsccPath
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$IsccPath) {
    $taskCandidates = @(
        (Join-Path $taskRoot '.build/tools/inno/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    )
    $IsccPath = $taskCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6.7+ or pass -IsccPath.'
}
if (!$SkipPublish) {
    & (Join-Path $PSScriptRoot 'build.ps1') -SkipTests:$SkipTests
}
$taskPublish = Join-Path $taskRoot 'artifacts/win-x64'
$taskExe = Join-Path $taskPublish 'DesktopNotes.exe'
if (!(Test-Path -LiteralPath $taskExe)) { throw 'Publish DesktopNotes before building the installer.' }
[xml]$taskProject = Get-Content -LiteralPath (Join-Path $taskRoot 'src/DesktopNotes.Presentation/DesktopNotes.Presentation.csproj')
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($taskVersion)) { throw 'App version is missing.' }
$taskFileVersion = (Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion.Split('+')[0]
if ($taskFileVersion -ne $taskVersion) { throw 'Published app version does not match the source. Publish again.' }
& $IsccPath ("/DAppVersion=" + $taskVersion) ("/DPublishDir=" + $taskPublish) ("/DOutputDirPath=" + (Join-Path $taskRoot 'artifacts')) (Join-Path $taskRoot 'installer/DesktopNotes.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$taskArtifacts = @('DesktopNotes-Setup.exe', 'DesktopNotes-win-x64.zip')
$taskChecksums = foreach ($name in $taskArtifacts) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $taskRoot ('artifacts/' + $name)) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
$taskChecksums | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts/SHA256SUMS.txt') -Encoding utf8
