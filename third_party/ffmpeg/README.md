# FFmpeg / LAME encoder provenance

The application invokes the vendored `bin/win-x64/ffmpeg.exe` as an external process. It is a minimal static Windows x64 build of upstream FFmpeg revision `92611900bb` (8.1.3 + 24 commits) and official LAME 3.100, without application-specific source patches. It is LGPL-2.1-or-later; `binary-license.txt` contains the binary's declaration. No GPL or nonfree component is enabled.

- Executable: 1,577,472 bytes; SHA256 `69fcfedfd0a893da4605c59bc7032b7649030a35de27bd68c6c761f7c09e1211`.
- FFmpeg source: https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/92611900bb ; SHA256 `950fe4283a24501ab004375c6aa08705051699b6eea7db7dd19fc21e8bc27cec`.
- LAME source: https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz ; SHA256 `ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e`.
- Build recipe: `tools/build-minimal-ffmpeg.sh`; verified source bootstrap and license/source packaging: `tools/prepare-minimal-encoder.ps1`.
- Compiler and package versions: `toolchain.txt`. Runtime notices: `toolchain-licenses/`. Exact compiler/configuration output: `build-configuration.txt`.

Normal builds require no compiler installation or encoder download: `tools/prepare-encoders.ps1` verifies the vendored executable hash and atomically installs it with notices into `artifacts/encoders`. The executable requires only Windows system DLLs. All encoder notices are carried into publish outputs; the vendored binary is excluded from the licenses tree to avoid a duplicate binary copy.

Only float32 PCM input, FLAC/LAME encoding, FLAC/MP3 muxing, file/pipe protocols and required audio conversion filters are retained. The binary has no compressed-audio decoders. `tools/prepare-encoders.ps1 -ForTests` optionally downloads the former universal BtbN binary to `artifacts/verification-decoder`, using the original archive/binary hashes and bounded verified downloader. `tools/check-audio.ps1` prepares this independent decoder only for encoding checks, unless PORTABLE_RECORDER_DECODER is supplied. `upstream-release.txt` and `COPYING.GPLv3` are retained records for that optional former BtbN dependency, not the production minimal executable.

For external redistribution, provide the corresponding source archives, build recipe and license notices alongside the binary or as an accompanying source package. `prepare-minimal-encoder.ps1` creates this package content in `artifacts/minimal-encoders`; source archives need not be embedded in the application's EXE. Rebuilding requires the documented MSYS2 MINGW64 toolchain; an ordinary application build uses the vetted vendored executable. Source pins and component selection are reproducible; byte-identical rebuilding additionally requires the same toolchain/environment.

See `docs/minimal-ffmpeg.md` for the verification matrix and measured byte sizes. Product encoding/clipping/failure code is unchanged.