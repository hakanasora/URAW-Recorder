# Minimal Windows encoder

The recorder feeds interleaved float32 little-endian PCM through `pipe:0` and writes a seekable sibling file. The minimal build retains the PCM demuxer/decoder, file and pipe protocols, FLAC and libmp3lame encoders, FLAC and MP3 muxers, and audio format conversion filters. It uses the existing FFmpeg implementation, including FLAC final MD5/total-sample updates and MP3 Xing/LAME gapless metadata. Application clipping and failure handling are unchanged.

Source pins:

| Component | Source | SHA-256 |
| --- | --- | --- |
| FFmpeg 8.1.3 + 24 commits | `https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/92611900bb` | `950fe4283a24501ab004375c6aa08705051699b6eea7db7dd19fc21e8bc27cec` |
| LAME 3.100 | `https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz` | `ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e` |

FFmpeg preserves the prior binary's source revision. LAME uses the official 3.100 release and is built statically from source; the MSYS2 LAME binary package is not linked.

Use a portable MSYS2 MINGW64 environment. Install `make mingw-w64-x86_64-gcc mingw-w64-x86_64-nasm mingw-w64-x86_64-pkgconf` with pacman, then run:

```powershell
./tools/prepare-minimal-encoder.ps1 -MsysRoot /path/to/msys64
```

Ordinary application builds use the vetted executable in `third_party/ffmpeg/bin/win-x64` and need no compiler or encoder download. The source rebuild script verifies source hashes before extraction and invokes `tools/build-minimal-ffmpeg.sh`. Outputs stay in `artifacts/minimal-encoders`; existing encoder and release directories are untouched. The output includes licenses, configure records, source archives and the build recipe. Its `source` folder preserves the `tools`, `docs`, and `artifacts/minimal-build` layout: extract the accompanying source archive, then invoke its `tools/prepare-minimal-encoder.ps1 -MsysRoot ...` with an installed toolchain to rebuild without source downloads. Rebuilding with the same source pins and options reproduces the component selection; byte-identical executables require the same compiler, binutils and build environment.

For verification, use the existing full FFmpeg as the independent decoder. The minimal executable intentionally has no FLAC/MP3 decoders:

```powershell
./tools/check-audio.ps1 -Group encoding
```

Distribute the source folder, recipe, and license texts alongside the encoder. FFmpeg is configured without GPL/nonfree components and is an external process. The FFmpeg executable statically links LGPL code, so retain its corresponding source and build instructions. Toolchain runtime notices must also accompany redistribution where required by the installed toolchain.

## Verified candidate (2026-11-07)

The original executable is 134,093,824 bytes. The candidate is 1,577,472 bytes, a reduction of 132,516,352 bytes (98.82%). Candidate SHA-256: `69fcfedfd0a893da4605c59bc7032b7649030a35de27bd68c6c761f7c09e1211`.

The encoding group passed 1,580 assertions with the baseline encoder and again with the candidate encoder and baseline decoder. It exercises all FLAC quality levels, channels 1–8 with representative sample rates including the accepted bounds 1 and 655350 Hz, all nine MP3 sample rates and both channel counts at every MP3 quality, exact FLAC round trips, MP3 gapless lengths and signal error, clipping, invalid/nonfinite input, short/empty recordings, existing-file protection, process failure, bounded queues and engine pause/discard behavior. These are automated process/file checks, not live capture tests. The continuous FLAC sample-rate range is not exhaustively enumerated.

PE import inspection shows only `bcrypt.dll`, `KERNEL32.dll`, `msvcrt.dll`, and `SHELL32.dll`. No third-party runtime DLL is required. Build/test logs are in `artifacts/minimal-build/{ffmpeg-build,baseline-checks,minimal-checks}.log`.

The integrated `check-audio.ps1 -Group encoding` path also passed 1,580 assertions after recovery on 2026-11-08; log: `artifacts/minimal-build/integrated-checks.log`. It prepares the vendored encoder and optional pinned verification decoder automatically. A normal encoder preparation was separately run with network access replaced by an exception and succeeded. The ten offline verified-download checks passed.
