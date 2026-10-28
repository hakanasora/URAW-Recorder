param([string[]]$Group=@(),[switch]$List)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = $env:PORTABLE_RECORDER_DOTNET
if (-not $dotnetExecutable) { $dotnetExecutable = 'F:\Codex\.tools\portable-recorder\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExecutable)) { $dotnetExecutable = (Get-Command dotnet).Source }
Push-Location -LiteralPath $repoRoot
try {
    if (-not $List -and ($Group.Count -eq 0 -or 'encoding' -in ($Group -split ','))) {
        & (Join-Path $PSScriptRoot 'prepare-encoders.ps1')
    }
    $arguments=@()
    if ($List) { $arguments+= '--list' }
    elseif ($Group.Count -gt 0) { $arguments+= @('--group',($Group -join ',')) }
    & $dotnetExecutable run --project tests/PortableRecorder.Audio.Checks -c Release -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "Audio checks failed: $LASTEXITCODE" }
} finally { Pop-Location }
