# FFmpeg / LAME encoder provenance

This software uses an unmodified, separate FFmpeg executable under LGPL-3.0-or-later. It is not loaded or linked into the C# process. FFmpeg's built-in FLAC encoder and libmp3lame provide the two compressed recording formats; no codec implementation is copied into application code.

- Builder: https://github.com/BtbN/FFmpeg-Builds
- Build recipe revision: `b4a2b1d7cc241786eb8e2b588a7ade40cad48bbd`
- Recipe / patches: https://github.com/BtbN/FFmpeg-Builds/tree/b4a2b1d7cc241786eb8e2b588a7ade40cad48bbd
- Release: `autobuild-2026-10-26-14-25`, LGPL static Windows x64 8.1 variant
- FFmpeg reported version: `n8.1.3-24-g92611900bb-20261026`
- Archive: https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-26-14-25/ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-8.1.zip
- Archive SHA256: `9b1907a3871eed4564ee745f04cc53c8713f3db7b3e2bed7805da3521769901a`
- Executable SHA256: `866b788425174dd57a41da9938887aa6f6cd1826f0513aec6ec2969a9bba3486`
- Upstream FFmpeg source reference: https://github.com/FFmpeg/FFmpeg/tree/92611900bb
- LAME source / license: https://lame.sourceforge.io/ ; LGPL 2.0-or-later (LAME-COPYING).

`build-configuration.txt` preserves actual compiler/configuration; `binary-license.txt` preserves the executable's own license declaration. `LICENSE.txt` is the bundled LGPL3 text, supplemented by COPYING.GPLv3. BtbN's build-script MIT license is not the executable's license. This selected build has no `--enable-gpl` or `--enable-nonfree`; do not substitute a GPL or nonfree binary silently.

`tools/prepare-encoders.ps1` downloads the pinned archive once to ignored artifacts, verifies both archive and executable SHA256, and checks encoder availability. Native publish carries encoders/ffmpeg.exe plus licenses/ffmpeg notices. No PATH lookup or runtime download. Preserve this cache: upstream daily release retention may remove dated downloads; updating the pin is an explicit dependency update, never a silent fallback to latest. The large binary/cache are not stored in source Git.

This is a local development distribution. Before external binary redistribution, bundle the corresponding FFmpeg **and all enabled dependency** sources, patches and build instructions from this exact build; source links alone are not a complete source distribution. Follow https://ffmpeg.org/legal.html and each dependency license. No application-specific changes were made to FFmpeg/LAME.
