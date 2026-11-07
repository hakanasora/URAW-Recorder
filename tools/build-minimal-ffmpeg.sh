#!/usr/bin/env bash
# Run from the repository root in an MSYS2 MINGW64 shell.
set -euo pipefail
root="$PWD"
work="$root/artifacts/minimal-build"
prefix="$work/prefix"
sources="$work/FFmpeg-92611900bb"
mkdir -p "$prefix" "$work/ffmpeg-build" "$root/artifacts/minimal-encoders"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
if [[ ! -f "$prefix/lib/libmp3lame.a" ]]; then
  cd "$work/lame-3.100"
  ./configure --prefix="$prefix" --disable-shared --enable-static --disable-frontend --disable-analyzer-hooks
  make -j"${JOBS:-8}"
  make install
fi
cd "$work/ffmpeg-build"
"$sources/configure" --prefix="$prefix" --disable-everything --disable-autodetect \
  --disable-debug --disable-doc --disable-network --disable-avdevice --disable-swscale \
  --disable-ffplay --disable-ffprobe --enable-small --enable-static --disable-shared \
  --enable-libmp3lame --enable-encoder=flac,libmp3lame --enable-decoder=pcm_f32le \
  --enable-demuxer=pcm_f32le --enable-muxer=flac,mp3 --enable-protocol=file,pipe \
  --enable-filter=aresample,aformat,anull --pkg-config-flags=--static \
  --extra-cflags="-Os -I$prefix/include" --extra-ldflags="-static -L$prefix/lib"
make -j"${JOBS:-8}"
cp ffmpeg.exe "$root/artifacts/minimal-encoders/ffmpeg.exe"
cp "$sources/COPYING.LGPLv2.1" "$root/artifacts/minimal-encoders/LICENSE.txt"
cp "$work/lame-3.100/COPYING" "$root/artifacts/minimal-encoders/LICENSE-LAME.txt"
cp config.h config_components.h ffbuild/config.mak "$root/artifacts/minimal-encoders/"
