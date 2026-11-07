param([string[]]$Group=@(),[switch]$List)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = $env:PORTABLE_RECORDER_DOTNET
if (-not $dotnetExecutable) { $dotnetExecutable = (Get-Command dotnet).Source }
Push-Location -LiteralPath $repoRoot
$previousDecoder=$env:PORTABLE_RECORDER_DECODER
try {
    if (-not $List -and ($Group.Count -eq 0 -or 'encoding' -in ($Group -split ','))) {
        & (Join-Path $PSScriptRoot 'prepare-encoders.ps1')
        if (-not $env:PORTABLE_RECORDER_DECODER) {
            & (Join-Path $PSScriptRoot 'prepare-encoders.ps1') -ForTests
            $env:PORTABLE_RECORDER_DECODER=Join-Path $repoRoot 'artifacts/verification-decoder/ffmpeg.exe'
        }
    }
    $arguments=@()
    if ($List) { $arguments+= '--list' }
    elseif ($Group.Count -gt 0) { $arguments+= @('--group',($Group -join ',')) }
    & $dotnetExecutable run --project tests/PortableRecorder.Audio.Checks -c Release -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Audio checks failed: $LASTEXITCODE" }
} finally { $env:PORTABLE_RECORDER_DECODER=$previousDecoder; Pop-Location }
