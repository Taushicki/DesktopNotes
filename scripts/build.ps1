param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.build'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.build\packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    dotnet restore DesktopNotes.slnx --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build DesktopNotes.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        dotnet test DesktopNotes.slnx -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    dotnet restore src/DesktopNotes.Presentation/DesktopNotes.Presentation.csproj -r win-x64 --configfile NuGet.Config -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
    # Keep one current publish directory. Never replace files of a running instance.
    $taskPublishPath = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/win-x64'))
    $taskArtifactsPath = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts'))
    if ((Split-Path -Parent $taskPublishPath) -ne $taskArtifactsPath) { throw 'Unexpected publish path.' }
    $taskRunning = Get-Process DesktopNotes -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($taskPublishPath + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if ($taskRunning) { throw 'Close DesktopNotes through its tray menu before publishing.' }
    if (Test-Path -LiteralPath $taskPublishPath) {
        $taskResolved = (Resolve-Path -LiteralPath $taskPublishPath).Path
        if ($taskResolved -ne $taskPublishPath -or ((Get-Item -LiteralPath $taskResolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected publish directory link.' }
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    }
    dotnet publish src/DesktopNotes.Presentation/DesktopNotes.Presentation.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/win-x64 -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Compress-Archive -Path artifacts/win-x64/* -DestinationPath artifacts/DesktopNotes-win-x64.zip -Force
}
finally { Pop-Location }
