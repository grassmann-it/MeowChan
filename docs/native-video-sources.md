# Windows native video sources and notices

The Windows prerelease redistributes the non-GPL NuGet package
`VideoLAN.LibVLC.Windows` 3.0.24, selected x64 tree, through dynamic LibVLCSharp
bindings. The accompanying [component license texts](native-video-licenses.txt) contain collected
component licenses; [third-party notices](../THIRD-PARTY-NOTICES.md) contain the LGPL 2.1 text,
VideoLAN attribution and upstream AUTHORS/THANKS. These files accompany the
binary application, together with this source-availability record.

## Exact upstream mapping

- Native core version string: `3.0.24-rc1-0-g6de05adcba`.
- Official annotated release tag `3.0.24` resolves to commit
  `6de05adcbaf2e8b85fe86aad4169393098628119`. Thus the `rc1` text in the native
  build does not identify a different commit from the official 3.0.24 source.
  [Tag API](https://code.videolan.org/api/v4/projects/videolan%2Fvlc/repository/tags/3.0.24)
  and [exact commit](https://code.videolan.org/videolan/vlc/-/commit/6de05adcbaf2e8b85fe86aad4169393098628119).
- Official source archive `vlc-3.0.24.tar.xz`: SHA-256
  `e7cab503d1d7d5849b89d2cf0e1ee60d0ef6d012407791b644b9cfc0cc225fdf`, verified
  against the [published checksum](https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz.sha256).
- Official Windows archive `vlc-3.0.24-win64.7z`: SHA-256
  `1ed59c09152e78aff84663fa76efab26057716576db74332858810e5ef54ae1f`, verified
  against its published checksum. All 420 selected runtime/plugin/data files
  match that official distribution byte for byte. The two additional `.lib`
  files in the NuGet tree are MSVC import libraries, not loaded runtime code.
- NuGet SHA-256:
  `3da3f37ee2b2b10e5c788d830a3b513658c36521ea1b0f966ed86eb69f41ef3c`.
  The upstream native packaging scripts remove enumerated GPL plugins from the
  official VLC distribution for this non-GPL package. The source/notices
  inventory intentionally includes a superset of optional components and does
  not claim that every inventoried component is linked into MeowChan.

## Source access at the prerelease

Distribute `MeowChan-0.9.0-video-sources.tar.gz` **alongside the Windows ZIP on
that same GitHub prerelease**. It contains:

- The unchanged official VLC 3.0.24 source archive, including the build scripts,
  `contrib/src` component versions/checksums and downstream patches.
- The source inputs fetched by that exact release's contrib recipes, including
  source archives, standalone SDK headers and upstream Git revision metadata.
  The Windows-only WiX build-tool binary download is excluded.
- LibVLCSharp 3.10.2 source, covering both the core binding and Avalonia adapter.
- The source input SHA-256 manifest, upstream commit/tag evidence, this document,
  component licenses and VideoLAN AUTHORS/THANKS.

The [companion source manifest](native-video-source-manifest.json) records each fetched input's name, size, SHA-256
and available license-file paths. Standard contrib archives passed the recipes'
SHA-512 checks; Git-generated archives passed the pinned revision check. One
upstream recipe, Tremor, records an eight-character revision while its generic
checker expects forty characters. Its downloaded source uses `b56ffce0`, which
upstream resolves to `b56ffce0c0773ec5ca04c466bc00b1bbcaf65aef`; both metadata
records are retained. Tremor is the fixed-point fallback selected only without
an FPU, not a Windows x64 runtime requirement. The conservative source bundle
includes it nevertheless. No source build or binary reproducibility is claimed.
The source inputs and patches provide the editable corresponding upstream source;
they are not a promise that arbitrary local toolchains reproduce identical bytes.

The `wix3.5.2519.0.zip` input is an optional build-tool binary and is omitted from
the companion. It is neither a runtime component nor represented as source.
The recipe remains in the unmodified VLC source for anyone choosing to build the
upstream Windows installer. MeowChan's own ZIP packaging does not run WiX.

The upstream source locations remain useful mirrors:
[VLC source](https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz),
[contrib mirror](https://download.videolan.org/pub/contrib/), and
[LibVLCSharp 3.10.2](https://code.videolan.org/videolan/LibVLCSharp/-/tree/3.10.2).
The same-release companion provides source access without relying solely on
those external mirrors. Include its SHA-256 in the prerelease asset checksums.

## Building and replacing LGPL components

The official VLC source includes `INSTALL`, the Windows packaging/configuration
scripts under `extras/package/win32`, and contrib bootstrap/Makefiles under
`contrib`. The source companion's `contrib-inputs` directory supplies the inputs
that those recipes normally download; copy them into the extracted VLC source's
`contrib/tarballs` directory. Preserve the provided patches and checksum recipes.
Use the upstream Windows cross-compilation instructions and an appropriate
MinGW toolchain/Windows SDK; source retrieval itself does not install a compiler.

The official full VLC build contains more plugins than MeowChan's non-GPL
package. When rebuilding a compatible replacement, use an appropriate component
license selection and preserve the libVLC 3 ABI, x64 architecture and plugin
hierarchy. MeowChan does not require the VLC GUI executable.

Quit MeowChan, back up its complete `libvlc/win-x64` directory and replace that
tree with the compatible rebuilt library/plugins. Restart the application and
test the local video. These native files remain outside the .NET single-file
executable; there is no signature or application integrity check preventing their
replacement. Build the managed binding from the included LibVLCSharp source and
substitute the rebuilt compatible package reference if modifying that layer.
MeowChan's project and lockfiles supply the application build inputs. The
application license permits reverse engineering needed to debug modifications
to these LGPL components, as stated in `THIRD-PARTY-NOTICES.md`.

This record addresses the selected Windows package. The separate macOS signing
procedure documents signing and replacement considerations for macOS; it does
not assert that macOS signing, notarization or native acceptance has occurred.
