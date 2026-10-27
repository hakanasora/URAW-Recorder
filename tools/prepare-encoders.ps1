$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repoRoot 'artifacts/encoder-cache'
$destination = Join-Path $repoRoot 'artifacts/encoders'
$expected = '9B1907A3871EED4564EE745F04CC53C8713F3DB7B3E2BED7805DA3521769901A'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-26-14-25/ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-8.1.zip'
New-Item -ItemType Directory -Force $cache,$destination | Out-Null
$archive = Join-Path $cache 'ffmpeg.zip'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Encoder archive SHA-256 mismatch. Remove the incorrect cached archive and retry; no unverified executable was installed.' }
$unpacked = Join-Path $cache 'unpacked'
$folder = Join-Path $unpacked 'ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-8.1'
if (-not (Test-Path -LiteralPath (Join-Path $folder 'bin/ffmpeg.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force }
$executable = Join-Path $destination 'ffmpeg.exe'
if ((Get-FileHash -LiteralPath (Join-Path $folder 'bin/ffmpeg.exe') -Algorithm SHA256).Hash -ne '866B788425174DD57A41DA9938887AA6F6CD1826F0513AEC6EC2969A9BBA3486') { throw 'Cached encoder binary hash mismatch; restore it from the verified archive.' }
if (-not (Test-Path -LiteralPath $executable) -or (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne '866B788425174DD57A41DA9938887AA6F6CD1826F0513AEC6EC2969A9BBA3486') {
    Copy-Item -LiteralPath (Join-Path $folder 'bin/ffmpeg.exe') -Destination $executable -Force
}
Copy-Item -LiteralPath (Join-Path $folder 'LICENSE.txt') -Destination (Join-Path $destination 'LICENSE.txt') -Force
$flacInfo = & $executable -hide_banner -loglevel error -h encoder=flac 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($flacInfo -match '^Encoder flac ')) { throw 'FLAC encoder unavailable.' }
$mp3Info = & $executable -hide_banner -loglevel error -h encoder=libmp3lame 2>&1
if ($LASTEXITCODE -ne 0 -or -not ($mp3Info -match '^Encoder libmp3lame ')) { throw 'LAME encoder unavailable.' }
Write-Output 'Pinned FFmpeg LGPL encoder ready (FLAC / LAME MP3).'
