param([switch]$Launch,[switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = $env:PORTABLE_RECORDER_DOTNET
if (-not $dotnetExecutable) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($systemDotnet -and (& $systemDotnet.Source --list-sdks)) { $dotnetExecutable = $systemDotnet.Source }
}
if (-not $dotnetExecutable) { throw 'Install .NET SDK 10.0.402 or set PORTABLE_RECORDER_DOTNET to dotnet.exe.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    & (Join-Path $PSScriptRoot 'prepare-encoders.ps1')
    if ($SingleFile) {
        $singleOutput = Join-Path $repoRoot 'artifacts/singlefile'
        & $dotnetExecutable publish src/PortableRecorder.Desktop/PortableRecorder.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $singleOutput --nologo
        if ($LASTEXITCODE -ne 0) { throw "Single-file build failed with exit code $LASTEXITCODE" }
        $singleExe = Join-Path $singleOutput 'URAW Recorder.exe'
        Move-Item -LiteralPath (Join-Path $singleOutput 'PortableRecorder.Desktop.exe') -Destination $singleExe -Force
        if ($Launch) { Start-Process -FilePath $singleExe }
        return
    }
    & $dotnetExecutable publish src/PortableRecorder.Desktop/PortableRecorder.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/current-preview --nologo
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE" }
    $encoderOutput = Join-Path $repoRoot 'artifacts/current-preview/encoders'
    New-Item -ItemType Directory -Force $encoderOutput | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts/encoders/ffmpeg.exe') -Destination $encoderOutput -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts/encoders/LICENSE.txt') -Destination $encoderOutput -Force
    # A successful compile alone does not guarantee a runnable WinUI publish.
    # Compiled XAML may be embedded in the PRI; a loose App.xbf is not required.
    foreach ($asset in @('PortableRecorder.Desktop.exe', 'PortableRecorder.Desktop.pri')) {
        $publishedAsset = Join-Path $repoRoot ('artifacts\current-preview\' + $asset)
        if (-not (Test-Path -LiteralPath $publishedAsset -PathType Leaf)) {
            throw "Incomplete WinUI publish: missing $asset"
        }
    }
    if ($Launch) {
        Start-Process -FilePath (Join-Path $repoRoot 'artifacts\current-preview\PortableRecorder.Desktop.exe')
    }
}
finally { Set-Location -LiteralPath $originalLocation.Path }
