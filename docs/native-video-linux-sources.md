# Linux video runtime and source inventory

The bundled-VLC tar and AppImage contain a private Ubuntu 24.04 LibVLC 3 runtime.
The smaller system-VLC tar contains no LibVLC and requires a compatible system
runtime before startup. Both Linux x64 and ARM64 have their own native packages.
The AppImage contains the same bundled application payload as its bundled tar.

`linux-video-runtime.json` identifies the selected mode and CPU. In bundled mode
its `Files` inventory hashes every file beneath `libvlc/<runtime>/`.
`linux-video-runtime-inventory.json` additionally records original distro paths,
binary and source package versions, original and patched file hashes, private
ELF dependencies, host-library boundaries and required symbol versions. The
`license_files` inventory hashes the accompanying `linux-video-licenses/` tree.
These hashes provide local consistency checks, not an artifact signature.

`scripts/package-linux.py prepare-runtime` copies the selected local-video
plugins and their recursive dependencies. It verifies each original file against
the exact SHA-256-checked Ubuntu binary package, materializes SONAME filenames,
and sets an origin-relative RUNPATH on every private ELF. It includes demuxers
for WebM/Matroska, MP4/MOV and AVI and the distribution's FFmpeg decoder, as well
as the selected audio, conversion and native X11 output plugins. File extensions
do not guarantee that every contained codec/profile is decodable. Actual
playback, audio devices, graphics drivers and distribution support require their
own native acceptance results.

The separate `MeowChan-<version>-<runtime>-video-sources.tar.gz` companion
contains exact Ubuntu source descriptors, original sources and distro patches
for the libraries and plugins actually shipped. Source files are verified against
their `.dsc` SHA-256 records; `manifest.json` binds them to the native inventories
and application source commit. Copyright records and license texts accompany
the sources and bundled binaries. Source retrieval requires matching `deb-src`
indices. The source collector fails when an exact source version is unavailable;
it does not substitute a newer version.

The existing Windows/Mac video-source base is independent and is not sufficient
for these distro-patched Linux binaries. The Linux collector neither downloads
nor repacks that base. It separately includes the much smaller exact
LibVLCSharp 3.10.2 core/adapter source archive, verified against the existing
binding-source SHA-256 inventory.

The AppImage uses appimagetool 1.9.1 and type2-runtime 20251108, with separate
x86_64 and aarch64 binary SHA-256 pins in `scripts/package-linux.py`. The source
companion contains the exact runtime application commit, its build/patch recipes,
the recipe-pinned libfuse 3.15.0 sources and squashfuse 0.5.2 sources. This supplies
the runtime application and LGPL dependency sources for rebuilding and relinking.
The upstream Alpine 3.21 recipes also use permissively licensed musl, zlib and
zstd; historical package revisions are not asserted. This is not a promise of
bit-identical output from arbitrary build environments. The source manifest
records those distinctions explicitly.

To rebuild a distro component, unpack its `.dsc` using `dpkg-source -x`, install
the matching build dependencies and use the included distro rules, typically
`dpkg-buildpackage -b -uc -us`. Replace the corresponding private libraries and
plugins and retain compatible SONAMEs and origin-relative RUNPATHs. For a local
replacement, regenerate the runtime inventory's `files` hashes and the package
marker's `Files` hashes from those replacement bytes, recording locally rebuilt
provenance. The official preparation command verifies Ubuntu package bytes and
must not falsely attribute a local rebuild to an unchanged Ubuntu binary. The
offline packaging command can consume that explicitly regenerated local inventory.
The AppImage runtime
source contains its build instructions and LGPL-library patch; rebuild it with
the replacement static library and provide the resulting runtime to an adjusted
packaging pin. The unchanged source and packaging scripts allow those changes;
the official binary pins intentionally reject silently substituted components.

AppImage execution normally requires available FUSE mounting. When unavailable,
use `--appimage-extract-and-run`, or extract with `--appimage-extract` and run
`squashfs-root/AppRun`. Its statically linked runtime does not require libfuse2,
but does not lower the glibc requirements of the packaged application. The bundle
still uses the host display server, audio services and graphics drivers. Native
Ubuntu 24.04 is the packaging baseline; other distributions must be tested before
being claimed as supported.
