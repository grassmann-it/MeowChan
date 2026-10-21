# MeowChan 0.9.0 screenshot provenance

These 14 PNGs are unedited, byte-identical renders of the integrated MeowChan UI from source commit `226af46bd834238162e4fd627f3801894c4da923` (Git tree `66263ab62dfa67831368ee9dffb3e537cc600dad`). The capture used the frozen DesktopSmoke and application Debug DLLs built at that commit. All 14 images were visually reviewed from the current capture before promotion; their promoted hashes match the capture originals.

The run passed on Manjaro Linux in a private 3200×2000 Xvfb display with Openbox, private DBus/runtime directories and software rendering. Requested and measured RenderScaling were **1.0**. Main-window scenes use Simple/Dark/English, showing the updated header, Feather-icon footer navigation and Downloads Columns control; captures exclude native window decorations. About, favorites and export dialogs retain their actual client sizes. The fixed temporary paths visible in some dialogs belong solely to the isolated showcase workspace; there are no personal profiles or downloaded posts.

`ShowcaseData` seeds 56 synthetic archived threads and 173 captured posts with saved, original procedural Skia illustrations. Its HTTP handler rejects network requests. Downloads shows passive demonstration projections; the gallery, reader, archive, favorites and HTML/original exports use real local application services. The gallery image is marked as a favorite in the running UI, and the original-export result reports one copied file. The Reader shows archived posts and a disabled Live-follow control as appropriate for a thread without an active watch; the separate Replies image shows locally resolved replies. About shows the configured PayPal support link, Credits and the offline Feather license inventory.

This is **source-built UI evidence**. It does not establish native acceptance of extracted Windows, macOS or Linux release packages, physical-device playback, host integrations or live provider behavior.

## Reproduce

The source-built **Debug** DesktopSmoke DLL was prepared from commit `226af46bd834238162e4fd627f3801894c4da923` with a locked restore and build in the isolated artifacts directory:

```sh
dotnet restore tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path artifacts/r09-profile-smoke -r linux-x64 --locked-mode
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path artifacts/r09-profile-smoke -r linux-x64 --no-restore -v minimal
```

The frozen DLL was then captured in a fresh isolated output and temporary directory. The exact capture invocation was:

```sh
mkdir -p /tmp/meowchan-226af46-showcase/temp
TMPDIR=/tmp/meowchan-226af46-showcase/temp \
PATH=/tmp/meowchan-ui-tools/usr/bin:$PATH \
LD_LIBRARY_PATH=/tmp/meowchan-ui-tools/usr/lib \
XDG_DATA_DIRS=/tmp/meowchan-ui-tools/usr/share \
python3 scripts/run-desktop-smoke.py \
  --assembly /home/tomka/Desktop/oChan/artifacts/r09-profile-smoke/bin/MeowChan.DesktopSmoke/debug_linux-x64/MeowChan.DesktopSmoke.dll \
  --mode visual --scale 1 --output /tmp/meowchan-226af46-showcase/capture --timeout 180
```

The three `/tmp/meowchan-ui-tools` overrides supplied private GUI prerequisites for this host; systems with Xvfb, Openbox and DBus tools installed normally can omit them. Retained local evidence: `/tmp/meowchan-226af46-showcase/capture/results.json` (SHA-256 `2284068fd8cba303c16dec45350069f48cc286d3a586b897a585954714c60af6`) and `visual/output.log` (SHA-256 `3f6b06ab50a1522f1eaf49b37c04685ac65f07633410dfb487b54e9a4c8521c0`). The result records PASS, 1.0 scaling, normal window close and no binding errors. The visual log confirms seven complete reply buttons inside the expanded post viewport. The runner deletes its isolated demo profile after the capture. The capture DLL SHA-256 values were:

| Binary | SHA-256 |
| --- | --- |
| `MeowChan.dll` | `c873e2070ca952951542615e03f3986fe12cb0b1cae420ba4855dee45621dbb7` |
| `MeowChan.DesktopSmoke.dll` | `0741e4b3b66082b70b75ed99fa8e236bf2c838e1bac1e31ce86cda50d9e9e898` |
| `libmeowchan.dll` | `d01ba23cf76befdd5f5370cba1717b1f83e91b4ea8f24b7c1b6e3caa2c3fe305` |

## Original files

| Image | Pixels | Capture time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [About credits](v0.9.0-about-credits.png) | 800×660 | 2026-10-21T19:32:55+00:00 | `6e701e1d95429f8b028f292284fbf1fa42189364aaa59552e238a0b616e66d8b` |
| [About offline licenses](v0.9.0-about-licenses.png) | 800×660 | 2026-10-21T19:32:55+00:00 | `bfbe37ffaaff095610fd4952f47b6f842d24baf572299ead4a1ed732d7eb721d` |
| [About and support](v0.9.0-about.png) | 800×660 | 2026-10-21T19:32:55+00:00 | `c2f0c64dad76a960edc230fe3f6b64a9e0ecba47196cca042b57415538a6652c` |
| [Archive search and saved media](v0.9.0-archive.png) | 1440×920 | 2026-10-21T19:32:54+00:00 | `825343fce26ad6d0859a35f33eaca79abaeb3f69d5d6d888a83827e56955d281` |
| [Downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-21T19:32:53+00:00 | `bdbc729437325e461f2fc39f1fc56218f8c71404f7a64cd44b24efa1ccf3d5c7` |
| [Repeated HTML export](v0.9.0-export.png) | 1000×820 | 2026-10-21T19:32:55+00:00 | `89020a627b60467be186532785403188ecf21100d8aba79d9ad75f3c58e92445` |
| [Favorite original-export dialog](v0.9.0-favorites-original-export.png) | 600×300 | 2026-10-21T19:32:55+00:00 | `2aa4f4e161bf6e3c8695546a0db158220ac8f4899933e5d16f5960600f46aa6a` |
| [Favorite original-export result](v0.9.0-favorites-original-result.png) | 600×300 | 2026-10-21T19:32:55+00:00 | `72cd83b8268cfeb804c540c6fb6320ac7a7a0523ad37b3bfab1d11d0528c6847` |
| [Favorites overview](v0.9.0-favorites.png) | 760×580 | 2026-10-21T19:32:55+00:00 | `9c03edc0c05b973b16a7e3405c47644435c2d40e442bf4a284415a7204de9567` |
| [Gallery with saved-post context](v0.9.0-gallery.png) | 1440×920 | 2026-10-21T19:32:53+00:00 | `f529079542cf042e3885a1f542aab3b7c10c75c11540666e0025a5adf0e99f57` |
| [Reader with expanded replies](v0.9.0-reader-replies.png) | 1440×960 | 2026-10-21T19:32:54+00:00 | `16bc570e3ea055e9af4b91e6764040f8238c347a7172aa6ecc74b51d9d7fa301` |
| [Offline reader](v0.9.0-reader.png) | 1440×960 | 2026-10-21T19:32:54+00:00 | `5138e2a13450909951473bbfd1ce4b7817efeb6111f8f8fa222c442e7d868cef` |
| [Rules and effective values](v0.9.0-rules.png) | 1200×700 | 2026-10-21T19:32:55+00:00 | `a425429b2d0ea498ad5c415eb3bddcd8fca80b04e15aa0ba3a24f5753605fa09` |
| [Settings and reading preferences](v0.9.0-settings.png) | 1200×700 | 2026-10-21T19:32:55+00:00 | `c19ff5a9ac8b8044661c9e43591a01eb264dca303f51c91a4a73cba2a237f3f7` |
