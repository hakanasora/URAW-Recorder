param([switch]$ForTests)
$ErrorActionPreference = 'Stop'

function Get-VerifiedEncoderArchive {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$ExpectedHash,
        [ValidateRange(1,600)][int]$TimeoutSeconds = 60,
        [ValidateRange(1,5)][int]$Attempts = 3
    )
    if ((Test-Path -LiteralPath $Archive) -and (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -eq $ExpectedHash) { return }
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $temporary = $Archive + '.' + [guid]::NewGuid().ToString('N') + '.download'
        $failure = $null
        try {
            Invoke-WebRequest -Uri $Url -OutFile $temporary -TimeoutSec $TimeoutSeconds -ErrorAction Stop
            $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
            if ($actual -ne $ExpectedHash) { throw "Encoder archive SHA-256 mismatch: expected $ExpectedHash, got $actual. No unverified archive was installed." }
            [IO.File]::Move($temporary, $Archive, $true)
            return
        }
        catch {
            $failure = $_.Exception
            if ($attempt -eq $Attempts) { throw [IO.IOException]::new("Pinned encoder download failed after $Attempts attempts: $($failure.Message)", $failure) }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
        finally {
            try { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction Stop } }
            catch {
                if ($null -eq $failure) { throw }
                $failure.Data['EncoderDownload.CleanupFailure'] = $_.Exception.Message
            }
        }
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repoRoot 'artifacts/encoder-cache'
$destination = Join-Path $repoRoot 'artifacts/encoders'
if ($ForTests) {
    $destination = Join-Path $repoRoot 'artifacts/verification-decoder'
    $expected = '9B1907A3871EED4564EE745F04CC53C8713F3DB7B3E2BED7805DA3521769901A'
    $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-26-14-25/ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-8.1.zip'
    New-Item -ItemType Directory -Force $cache,$destination | Out-Null
    $archive = Join-Path $cache 'ffmpeg.zip'
    Get-VerifiedEncoderArchive -Url $url -Archive $archive -ExpectedHash $expected
    $unpacked = Join-Path $cache 'unpacked'
    $folder = Join-Path $unpacked 'ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-8.1'
    $binaryHash = '866B788425174DD57A41DA9938887AA6F6CD1826F0513AEC6EC2969A9BBA3486'
    $cachedBinary = Join-Path $folder 'bin/ffmpeg.exe'
    if (-not (Test-Path -LiteralPath $cachedBinary) -or (Get-FileHash -LiteralPath $cachedBinary -Algorithm SHA256).Hash -ne $binaryHash) {
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force
    }
} else {
    $folder = Join-Path $repoRoot 'third_party/ffmpeg'
    $cachedBinary = Join-Path $folder 'bin/win-x64/ffmpeg.exe'
    $binaryHash = '69FCFEDFD0A893DA4605C59BC7032B7649030A35DE27BD68C6C761F7C09E1211'
}
New-Item -ItemType Directory -Force $destination | Out-Null
$executable = Join-Path $destination 'ffmpeg.exe'
if ((Get-FileHash -LiteralPath $cachedBinary -Algorithm SHA256).Hash -ne $binaryHash) { throw 'Cached encoder binary hash mismatch; no unverified executable was installed.' }
if (-not (Test-Path -LiteralPath $executable) -or (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $binaryHash) {
    $binaryTemporary = $executable + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $failure = $null
    try {
        Copy-Item -LiteralPath $cachedBinary -Destination $binaryTemporary -ErrorAction Stop
        if ((Get-FileHash -LiteralPath $binaryTemporary -Algorithm SHA256).Hash -ne $binaryHash) { throw 'Copied encoder binary SHA-256 mismatch.' }
        [IO.File]::Move($binaryTemporary, $executable, $true)
    }
    catch { $failure = $_.Exception; throw }
    finally {
        try { if (Test-Path -LiteralPath $binaryTemporary) { Remove-Item -LiteralPath $binaryTemporary -Force -ErrorAction Stop } }
        catch {
            if ($null -eq $failure) { throw }
            $failure.Data['EncoderInstall.CleanupFailure'] = $_.Exception.Message
        }
    }
}
Copy-Item -LiteralPath (Join-Path $folder 'LICENSE.txt') -Destination (Join-Path $destination 'LICENSE.txt') -Force
if (-not $ForTests) {
    foreach ($notice in @('LAME-COPYING','binary-license.txt','build-configuration.txt','README.md','toolchain.txt','toolchain-licenses')) {
        Copy-Item -LiteralPath (Join-Path $folder $notice) -Destination $destination -Recurse -Force
    }
}
$flacInfo = & $executable -hide_banner -loglevel error -h encoder=flac 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($flacInfo -match '^Encoder flac ')) { throw 'FLAC encoder unavailable.' }
$mp3Info = & $executable -hide_banner -loglevel error -h encoder=libmp3lame 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($mp3Info -match '^Encoder libmp3lame ')) { throw 'LAME encoder unavailable.' }
Write-Output 'Pinned FFmpeg LGPL encoder ready (FLAC / LAME MP3).'
