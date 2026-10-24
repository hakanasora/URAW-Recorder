param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = $env:PORTABLE_RECORDER_DOTNET
if (-not $dotnetExecutable) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($systemDotnet -and (& $systemDotnet.Source --list-sdks)) { $dotnetExecutable = $systemDotnet.Source }
}
if (-not $dotnetExecutable) {
    $localSdk = 'F:\Codex\.tools\portable-recorder\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $dotnetExecutable = $localSdk }
}
if (-not $dotnetExecutable) { throw 'Install .NET SDK 10.0.402 or set PORTABLE_RECORDER_DOTNET to dotnet.exe.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$originalLocation = Get-Location
try {
    Set-Location -LiteralPath $repoRoot
    & $dotnetExecutable publish src/PortableRecorder.Desktop/PortableRecorder.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/native-preview --nologo
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE" }
    if ($Launch) {
        Start-Process -FilePath (Join-Path $repoRoot 'artifacts\native-preview\PortableRecorder.Desktop.exe')
    }
}
finally { Set-Location -LiteralPath $originalLocation.Path }
