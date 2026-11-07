param([string]$MsysRoot=(Join-Path $PSScriptRoot '../artifacts/minimal-build/msys64'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$work=Join-Path $root 'artifacts/minimal-build'
$output=Join-Path $root 'artifacts/minimal-encoders'
$bash=Join-Path $MsysRoot 'usr/bin/bash.exe'
if(-not(Test-Path -LiteralPath $bash)){throw 'Provide a portable MSYS2 MINGW64 installation with make, mingw-w64-x86_64-gcc, nasm and pkgconf using -MsysRoot.'}
New-Item -ItemType Directory -Force $work,$output | Out-Null
$sources=@(
    @{Name='ffmpeg-iwr.tar.gz'; Url='https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/92611900bb'; Hash='950FE4283A24501AB004375C6AA08705051699B6EEA7DB7DD19FC21E8BC27CEC'},
    @{Name='lame-3.100.tar.gz'; Url='https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz'; Hash='DDFE36CAB873794038AE2C1210557AD34857A4B6BDC515785D1DA9E175B1DA1E'}
)
foreach($source in $sources){
    $archive=Join-Path $work $source.Name
    if(-not(Test-Path -LiteralPath $archive)) {Invoke-WebRequest -Uri $source.Url -OutFile $archive}
    if((Get-FileHash -LiteralPath $archive).Hash -ne $source.Hash){throw "Source checksum mismatch: $archive"}
    & tar -xf $archive -C $work
    if($LASTEXITCODE -ne 0){throw "Source extraction failed: $archive"}
}
$oldMsystem=$env:MSYSTEM
Push-Location $root
try{
    $env:MSYSTEM='MINGW64'
    # MSYS bash converts the Windows working directory; no shell interpolation of paths.
    & $bash -c 'export PATH=/mingw64/bin:/usr/bin:$PATH; bash tools/build-minimal-ffmpeg.sh'
    if($LASTEXITCODE -ne 0){throw 'Minimal FFmpeg build failed.'}
}finally{Pop-Location; $env:MSYSTEM=$oldMsystem}
$sourceOutput=Join-Path $output 'source'
$sourceCache=Join-Path $sourceOutput 'artifacts/minimal-build'
$sourceTools=Join-Path $sourceOutput 'tools'
$sourceDocs=Join-Path $sourceOutput 'docs'
New-Item -ItemType Directory -Force $sourceCache,$sourceTools,$sourceDocs | Out-Null
foreach($source in $sources){Copy-Item -LiteralPath (Join-Path $work $source.Name) -Destination $sourceCache -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build-minimal-ffmpeg.sh') -Destination $sourceTools -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'prepare-minimal-encoder.ps1') -Destination $sourceTools -Force
Copy-Item -LiteralPath (Join-Path $root 'docs/minimal-ffmpeg.md') -Destination $sourceDocs -Force
Copy-Item -LiteralPath (Join-Path $root 'docs/minimal-ffmpeg.md') -Destination (Join-Path $output 'BUILD.md') -Force
$noticeOutput=Join-Path $output 'toolchain-licenses'
New-Item -ItemType Directory -Force $noticeOutput | Out-Null
foreach($component in @('crt','headers','gcc','libgcc','libwinpthread','winpthreads')){
    $notices=Join-Path $MsysRoot "mingw64/share/licenses/$component"
    if(Test-Path -LiteralPath $notices){Copy-Item -LiteralPath $notices -Destination $noticeOutput -Recurse -Force}
}
& $bash -c 'export PATH=/mingw64/bin:/usr/bin:$PATH; pacman -Q; gcc --version; ld --version' | Set-Content (Join-Path $output 'toolchain.txt')
Get-ChildItem -LiteralPath $output -File -Recurse | Get-FileHash | Format-Table -AutoSize
