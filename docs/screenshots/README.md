# MeowChan 0.9.0 screenshot provenance

These 14 PNGs are unedited, byte-identical renders of the integrated MeowChan UI from source commit `b8e129146e1d8d3d26108fbee00aa881ae2ac5ea` (Git tree `9ce0630e1f649c3d1bf0b0a3e5a65f52e6743f9c`). The capture used the frozen DesktopSmoke and application Debug DLLs built at that commit. All 14 images were visually reviewed from the current capture before promotion; their promoted hashes match the capture originals.

The run passed on Manjaro Linux in a private 3200×2000 Xvfb display with Openbox, private DBus/runtime directories and software rendering. Requested and measured RenderScaling were **1.0**. Main-window scenes use Simple/Dark/English, showing the updated header, Feather-icon footer navigation and Downloads Columns control; captures exclude native window decorations. About, favorites and export dialogs retain their actual client sizes. The fixed temporary paths visible in some dialogs belong solely to the isolated showcase workspace; there are no personal profiles or downloaded posts.

`ShowcaseData` seeds 56 synthetic archived threads and 173 captured posts with saved, original procedural Skia illustrations. Its HTTP handler rejects network requests. Downloads shows passive demonstration projections; the gallery, reader, archive, favorites and HTML/original exports use real local application services. The gallery image is marked as a favorite in the running UI, and the original-export result reports one copied file. The Reader shows archived posts and a disabled Live-follow control as appropriate for a thread without an active watch; the separate Replies image shows locally resolved replies. About shows the configured PayPal support link, Credits and the offline Feather license inventory.

This is **source-built UI evidence**. It does not establish native acceptance of extracted Windows, macOS or Linux release packages, physical-device playback, host integrations or live provider behavior.

## Reproduce

The source-built **Debug** DesktopSmoke DLL was prepared from commit `b8e129146e1d8d3d26108fbee00aa881ae2ac5ea` with a locked restore and build in the isolated artifacts directory:

```sh
dotnet restore tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path artifacts/r09-showcase-b8e1291 -r linux-x64 --locked-mode
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path artifacts/r09-showcase-b8e1291 -r linux-x64 --no-restore -v minimal
```

The frozen DLL was then captured in a fresh isolated output and temporary directory. The short temporary path keeps private DBus socket addresses within the Unix path limit. The exact capture invocation was:

```sh
mkdir -p /tmp/mc-b8-t
TMPDIR=/tmp/mc-b8-t \
PATH=/tmp/meowchan-ui-tools/usr/bin:$PATH \
LD_LIBRARY_PATH=/tmp/meowchan-ui-tools/usr/lib \
XDG_DATA_DIRS=/tmp/meowchan-ui-tools/usr/share:/usr/share \
python3 scripts/run-desktop-smoke.py \
  --assembly /home/tomka/Desktop/oChan/artifacts/r09-showcase-b8e1291/bin/MeowChan.DesktopSmoke/debug_linux-x64/MeowChan.DesktopSmoke.dll \
  --mode visual --scale 1 --output artifacts/r09-showcase-b8e1291/capture2 --timeout 180
```

The three `/tmp/meowchan-ui-tools` overrides supplied private GUI prerequisites for this host; systems with Xvfb, Openbox and DBus tools installed normally can omit them. Retained local evidence: `artifacts/r09-showcase-b8e1291/capture2/results.json` (SHA-256 `2284068fd8cba303c16dec45350069f48cc286d3a586b897a585954714c60af6`) and `visual/output.log` (SHA-256 `0f7d2fcd42e4af06415c279a2c15992c9e4a1dd70b5a0e7ea9a1dc714d4625d8`). The result records PASS, 1.0 scaling, normal window close and no binding errors. The visual log confirms seven complete reply buttons inside the expanded post viewport. The runner deletes its isolated demo profile after the capture. The capture DLL SHA-256 values were:

| Binary | SHA-256 |
| --- | --- |
| `MeowChan.dll` | `6f8ab3e847c4743931b26244cca5179a151f51c95fcb14a6cb93bf0bf52d8eb3` |
| `MeowChan.DesktopSmoke.dll` | `3e685a5bdb235f8651039e6623dbd7bb4d897cae66fbb1e0df8584de0c954993` |
| `libmeowchan.dll` | `3b4a7324ea18fc81d7488b1f53180b514ffda6e5d5da1ebec92f55dba3baa713` |

## Original files

| Image | Pixels | Capture time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [About credits](v0.9.0-about-credits.png) | 800×660 | 2026-10-21T20:00:30+00:00 | `6e701e1d95429f8b028f292284fbf1fa42189364aaa59552e238a0b616e66d8b` |
| [About offline licenses](v0.9.0-about-licenses.png) | 800×660 | 2026-10-21T20:00:30+00:00 | `bfbe37ffaaff095610fd4952f47b6f842d24baf572299ead4a1ed732d7eb721d` |
| [About and support](v0.9.0-about.png) | 800×660 | 2026-10-21T20:00:30+00:00 | `c2f0c64dad76a960edc230fe3f6b64a9e0ecba47196cca042b57415538a6652c` |
| [Archive search and saved media](v0.9.0-archive.png) | 1440×920 | 2026-10-21T20:00:29+00:00 | `825343fce26ad6d0859a35f33eaca79abaeb3f69d5d6d888a83827e56955d281` |
| [Downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-21T20:00:27+00:00 | `626e7fbb88df0075b46c498cc910f8ccd7aacaa563655ed93c99e351a74cd692` |
| [Repeated HTML export](v0.9.0-export.png) | 1000×820 | 2026-10-21T20:00:29+00:00 | `ec560e1537a466da430865120e6565064b3ed6a57bfef4e8136878bf5e7c0657` |
| [Favorite original-export dialog](v0.9.0-favorites-original-export.png) | 600×300 | 2026-10-21T20:00:29+00:00 | `55997bfae9f85add91e7e95074ddb30bc09488f793ffe50b6287c376549859c1` |
| [Favorite original-export result](v0.9.0-favorites-original-result.png) | 600×300 | 2026-10-21T20:00:29+00:00 | `c181a0b20a528d2c9d1bb5d4203221474788a1f963598bc4e0e8334f40698ce0` |
| [Favorites overview](v0.9.0-favorites.png) | 760×580 | 2026-10-21T20:00:29+00:00 | `9c03edc0c05b973b16a7e3405c47644435c2d40e442bf4a284415a7204de9567` |
| [Gallery with saved-post context](v0.9.0-gallery.png) | 1440×920 | 2026-10-21T20:00:28+00:00 | `f529079542cf042e3885a1f542aab3b7c10c75c11540666e0025a5adf0e99f57` |
| [Reader with expanded replies](v0.9.0-reader-replies.png) | 1440×960 | 2026-10-21T20:00:28+00:00 | `16bc570e3ea055e9af4b91e6764040f8238c347a7172aa6ecc74b51d9d7fa301` |
| [Offline reader](v0.9.0-reader.png) | 1440×960 | 2026-10-21T20:00:28+00:00 | `5138e2a13450909951473bbfd1ce4b7817efeb6111f8f8fa222c442e7d868cef` |
| [Rules and effective values](v0.9.0-rules.png) | 1200×700 | 2026-10-21T20:00:29+00:00 | `a425429b2d0ea498ad5c415eb3bddcd8fca80b04e15aa0ba3a24f5753605fa09` |
| [Settings and reading preferences](v0.9.0-settings.png) | 1200×700 | 2026-10-21T20:00:29+00:00 | `c19ff5a9ac8b8044661c9e43591a01eb264dca303f51c91a4a73cba2a237f3f7` |
