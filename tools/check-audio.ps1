$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = $env:PORTABLE_RECORDER_DOTNET
if (-not $dotnetExecutable) { $dotnetExecutable = 'F:\Codex\.tools\portable-recorder\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExecutable)) { $dotnetExecutable = (Get-Command dotnet).Source }
Push-Location -LiteralPath $repoRoot
try {
    & $dotnetExecutable run --project tests/PortableRecorder.Audio.Checks -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Audio checks failed: $LASTEXITCODE" }
} finally { Pop-Location }
