# macOS native video sources, notices and replacement

The macOS app redistributes `VideoLAN.LibVLC.Mac` **3.1.3.2**, one selected tree
per RID (`osx-arm64` or `osx-x64`). This is a packaging version: both cores are
VLC **3.0.24**, revision `3.0.24-rc1-0-g6de05adcba`, exact upstream commit
`6de05adcbaf2e8b85fe86aad4169393098628119`. LibVLCSharp and its Avalonia adapter
are **3.10.2**. Copyright belongs to VideoLAN, LibVLCSharp and the respective
component contributors. MeowChan's own license remains Artistic 2.0.

## Component notices

LibVLC core and LibVLCSharp use LGPL-2.1-or-later; the full LGPL text is in
[third-party notices](../THIRD-PARTY-NOTICES.md). The Mac package's aggregate
NuSpec expression does not override the licenses of individual modules. These
four retained plugins declare **GPL-2.0-or-later** in both architectures:

| Plugin | Source within `vlc-3.0.24.tar.xz` |
| --- | --- |
| `libdolby_surround_decoder_plugin.dylib` | `modules/audio_filter/channel_mixer/dolby.c` |
| `libheadphone_channel_mixer_plugin.dylib` | `modules/audio_filter/channel_mixer/headphone.c` |
| `libsyslog_plugin.dylib` | `modules/logger/syslog.c` |
| `liblua_plugin.dylib` | `modules/lua/vlc.c` and the `modules/lua/Makefile.am` source list |

The [full GPL v2 text](native-video-macos-COPYING) accompanies the app and source
overlay. The source headers declare `VLC_LICENSE_GPL_2_PLUS`; their hashes and
immutable upstream links are recorded in the [Mac source manifest](native-video-macos-source-manifest.json).
The plugin set is retained. No blanket LGPL-only claim is made for the Mac tree.
Third-party components retain their individual copyright/license terms; the
[collected component texts](native-video-licenses.txt), upstream source headers,
`COPYING`, `COPYING.LIB`, `AUTHORS` and `THANKS` accompany the source base.
The conservative source inventory includes optional components beyond those
linked into the selected Mac package.

## Two source assets for each binary release

Offer both assets alongside the matching binary DMGs and include all hashes in
the same release checksums. Upstream URLs alone are not the source mechanism.

1. The unchanged **`MeowChan-0.9.0-video-sources.tar.gz`**, 725131331 bytes,
   SHA-256 `bf556da3dd1fb511dafb81f684a0bb6fc38e7cb7b91081e9ac0c307bd0771bfa`.
   Its historical filename and Windows-oriented README do not change its payload:
   exact VLC source, 126 checksum-pinned source/header inputs, six Git-pinned
   source inputs, all upstream contrib recipes/patches and LibVLCSharp source.
   The Windows installer-only WiX binary is excluded; it is not a Mac source input.
2. **`MeowChan-<version>-macos-video-source-overlay.tar.gz`**, generated for the
   exact committed application source and final binary DMGs. It contains the
   complete editable application source as `application-source.tar`, exact
   packaging/signing scripts and locks, these notices, original-to-packaged
   native-file hashes per RID, public DMG hashes, internal inventory ZIP hashes and an internal checksum
   inventory. Its generated `manifest.json` identifies the immutable base.

The generator archives committed Git contents only; local or ignored files are
not included. The base remains unchanged and is neither duplicated nor repacked.
The overlay can cover one or both architectures; its manifest lists exactly which
DMGs it covers. Native validation reports can be retained alongside their hashes;
the generator does not infer Apple signature validity, playback or Gatekeeper
acceptance from file hashes.

Example, run after the final source changes are committed and the matching DMGs
have passed native validation (substitute an exact 40-character commit):

```sh
python3 scripts/package-macos-sources.py \
  --source-commit <exact-commit> --version 0.9.0 \
  --base /path/to/MeowChan-0.9.0-video-sources.tar.gz \
  --binary-zip osx-arm64=/path/to/MeowChan-0.9.0-osx-arm64.zip \
  --binary-zip osx-x64=/path/to/MeowChan-0.9.0-osx-x64.zip \
  --binary-dmg osx-arm64=/path/to/MeowChan-0.9.0-osx-arm64.dmg \
  --binary-dmg osx-x64=/path/to/MeowChan-0.9.0-osx-x64.dmg \
  --validation-report osx-arm64=/path/to/arm64-native-validation.json \
  --validation-report osx-x64=/path/to/intel-native-validation.json \
  --output /path/to/MeowChan-0.9.0-macos-video-source-overlay.tar.gz
```

The command binds each final DMG hash and verifies the base hash, each internal inventory ZIP's bundle version, the complete
native path set (including the retained GPL plugins), unchanged native data, and
byte-identical source notices in each ZIP. It records final library hashes after
load-command repairs and ad-hoc signing. It rejects stale/missing notices and
existing output paths. Fixed member metadata, ordering and gzip metadata make
identical committed inputs and binary/report bytes produce identical overlays.
This is an offline preparation command; it does not publish anything. Review the
generated manifest/inventories before promoting exact release artifacts.

## Evidence and its limits

The Mac manifest records all **397 ARM and 403 Intel input paths**. Every selected
NuGet file is byte-identical to its mapped path in the official checksum-verified
VLC 3.0.24 DMG. The pinned upstream repackaging helper and its provenance are in
`scripts/native-video`; that helper is evidence and is not run by the overlay
generator. MeowChan changes selected `LC_LOAD_DYLIB` core references from
`@rpath/libvlccore.dylib` to relative `@loader_path` paths, then ad-hoc signs
Mach-O files and the outer bundle. It does not edit VLC source or remove plugins.

All **86 pkg-config identities** in the official same-commit contrib snapshots
map to existing base source inputs: 74 direct recipe-version matches and 12
documented source/ABI version aliases. Runtime strings in eight actual plugin
families corroborate source versions on both architectures. No concrete missing
source input or source-version mismatch was identified in that reviewed set.
Not every static dependency was separately fingerprinted. The snapshots are
corroborating candidates, not proof of which prebuilt archive the release used.

The original release build invocation, exact toolchain and selected prebuilt
snapshot remain unproven; the Intel candidate says Darwin18 while the release
core configure string says Darwin19. Public job traces were unavailable. These
are rebuild-provenance uncertainties, not an assertion of missing corresponding
source merely because an exact CI trace is unavailable. No bit-identical rebuild
or legal certification is claimed.

## Build and replace native components

Verify both source-asset checksums, extract the base and overlay into separate
directories, then extract `application-source.tar` into an application source
directory. The base root `MeowChan-video-sources` contains `vlc-3.0.24.tar.xz`,
`contrib-inputs/` and `libvlcsharp-3.10.2.tar.gz`. Extract the VLC tarball and copy
the contents of `contrib-inputs/` into `vlc-3.0.24/contrib/tarballs/`.
Preserve upstream checksums, patches and Git revision metadata.

Use a Mac with Xcode command-line tools, a suitable SDK and the tools described
by upstream `INSTALL` and `extras/package/macosx/build.sh`. From the extracted
VLC source, `extras/package/macosx/build.sh -c -a arm64` or `-c -a x86_64` selects
the architecture and builds contrib from source rather than silently taking a
prebuilt snapshot. Read that script's options and `configure.sh` for your chosen
toolchain; source availability is not a guarantee of compatibility with arbitrary
new SDKs. The exact release recipes and patches are included. The official build
may produce additional GUI/plugins; MeowChan needs a compatible **libVLC 3 ABI**,
the selected architecture and matching plugin/data hierarchy, not the VLC GUI.
Keep the licenses/notices for the components you rebuild and redistribute.

Quit MeowChan and back up the entire app. Its replaceable native tree is physically
`MeowChan.app/Contents/Resources/application/libvlc/<RID>/lib`, with `libvlc.dylib`,
`libvlccore.dylib`, `vlc/plugins/` and `vlc/share/`. Preserve this layout when
substituting compatible rebuilt libraries/plugins. `Contents/MacOS/libvlc` is a
relative link to the payload; edit the real tree. For a clean repack, replace
`libvlc/<RID>` in an unsigned `dotnet publish` directory before invoking
`scripts/package-macos.py`; it performs the core load-command repair before
signing. No C launcher is required.

For a modified installed app, run on the matching Mac:

```sh
python3 scripts/macos-signing.py /path/to/MeowChan.app --runtime osx-arm64 --sign-adhoc
```

Use `osx-x64` for Intel. This repairs the supported core references, signs nested
Mach-O files and the bundle, applies the apphost CoreCLR entitlements and validates
architecture, signatures and dependency closure. Replacing files changes bundle
signatures; no Apple certificate is needed for local ad-hoc signing. Restart and
test local playback, plugin loading and shutdown. Follow [macOS signing and
manual approval](macos-signing.md) for per-app Gatekeeper approval; retain system
protections and quarantine metadata.

For managed-binding changes, extract the included LibVLCSharp source, build the
3.10.2 core/Avalonia projects with a compatible SDK, and substitute the rebuilt
packages/references in the application's project and Release lockfiles. For the
application, use the SDK in `global.json` and the committed CI workflow's
lockfile selection/publish flags. For example, from the application source root:

```sh
dotnet restore MeowChan.csproj -p:RuntimeIdentifier=osx-arm64 -p:Configuration=Release \
  --locked-mode -p:SelfContained=true -p:PublishSingleFile=false -p:EnableSingleFileAnalyzer=true
dotnet publish MeowChan.csproj -c Release -r osx-arm64 --self-contained true \
  --no-restore -p:PublishSingleFile=false -p:EnableSingleFileAnalyzer=true --output publish-output
python3 scripts/package-macos.py --publish-dir publish-output --output-dir packaged \
  --runtime osx-arm64 --version 0.9.0 --sign-adhoc --archive MeowChan-0.9.0-osx-arm64.zip
python3 scripts/package-macos-dmg.py --bundle packaged/MeowChan.app \
  --dmg MeowChan-0.9.0-osx-arm64.dmg --copied-root installed-from-dmg --runtime osx-arm64
```

Use the chosen source version and RID consistently. After modifying dependencies,
update the corresponding Release lockfile rather than bypassing locked restore.
The application permits compatible component replacement and reverse engineering
needed to debug modifications to LGPL components; it imposes no integrity check
requiring the original native component bytes.
