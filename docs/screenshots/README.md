# MeowChan 0.9.0 screenshot provenance

These 14 PNGs are unedited, byte-identical renders of the integrated MeowChan UI from source commit `115954305da680953e8e045c881618e1f6601056` (Git tree `c15412311d1696a65e0a60e27e0b46e5fdd93c75`). The capture used the frozen DesktopSmoke and application Debug DLLs built at that commit. All 14 images were visually reviewed from the current capture before promotion; their promoted hashes match the capture originals.

The run passed on Manjaro Linux in a private 3200×2000 Xvfb display with Openbox, private DBus/runtime directories and software rendering. Requested and measured RenderScaling were **1.0**. Main-window scenes use Simple/Dark/English; captures exclude native window decorations. About, favorites and export dialogs retain their actual client sizes. The fixed temporary paths visible in some dialogs belong solely to the isolated showcase workspace; there are no personal profiles or downloaded posts.

`ShowcaseData` seeds 56 synthetic archived threads and 173 captured posts with saved, original procedural Skia illustrations. Its HTTP handler rejects network requests. Downloads shows passive demonstration projections; the gallery, reader, archive, favorites and HTML/original exports use real local application services. The gallery image is marked as a favorite in the running UI, and the original-export result reports one copied file. The Reader shows archived posts and a disabled Live-follow control as appropriate for a thread without an active watch; the separate Replies image shows locally resolved replies. About shows the configured PayPal support link, Credits and the offline Feather license inventory.

This is **source-built UI evidence**. It does not establish native acceptance of extracted Windows, macOS or Linux release packages, physical-device playback, host integrations or live provider behavior.

## Reproduce

The source-built **Debug** DesktopSmoke DLL was prepared from commit `115954305da680953e8e045c881618e1f6601056` with a locked restore followed by:

```sh
dotnet restore tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path /tmp/meowchan-final-showcase-settled/build -r linux-x64 --locked-mode
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --artifacts-path /tmp/meowchan-final-showcase-settled/build -r linux-x64 --no-restore -v minimal
```

The frozen DLL was then captured in a fresh isolated output and temporary directory. The exact capture invocation was:

```sh
mkdir -p /tmp/meowchan-final-showcase-settled/temp
TMPDIR=/tmp/meowchan-final-showcase-settled/temp \
PATH=/tmp/meowchan-ui-tools/usr/bin:$PATH \
LD_LIBRARY_PATH=/tmp/meowchan-ui-tools/usr/lib \
XDG_DATA_DIRS=/tmp/meowchan-ui-tools/usr/share \
python3 scripts/run-desktop-smoke.py \
  --assembly /tmp/meowchan-final-showcase-settled/build/bin/MeowChan.DesktopSmoke/debug_linux-x64/MeowChan.DesktopSmoke.dll \
  --mode visual --scale 1 --output /tmp/meowchan-final-showcase-settled/capture --timeout 180
```

The three `/tmp/meowchan-ui-tools` overrides supplied private GUI prerequisites for this host; systems with Xvfb, Openbox and DBus tools installed normally can omit them. Retained local evidence: `/tmp/meowchan-final-showcase-settled/capture/results.json` (SHA-256 `2284068fd8cba303c16dec45350069f48cc286d3a586b897a585954714c60af6`) and `visual/output.log` (SHA-256 `cf9608c4beb97eed074f553bfe8507dd66a886d7ac120777941c23bf5f9aa31d`). The result records PASS, 1.0 scaling, normal window close and no binding errors. The visual log confirms seven complete reply buttons inside the expanded post viewport. The runner deletes its isolated demo profile after the capture. The capture DLL SHA-256 values were:

| Binary | SHA-256 |
| --- | --- |
| `MeowChan.dll` | `397f2c014877707428629800b491d209e52e1b993177207402e36d08bfebe2d0` |
| `MeowChan.DesktopSmoke.dll` | `47806af6c8c9b0fd9ba2bd409603fdc5652869bc126f02adf4175aee56301b51` |
| `libmeowchan.dll` | `256887c4305af767628b9bfd1c69bff20ce39820a4b2870533ef1e5f9bed4a40` |

## Original files

| Image | Pixels | Capture time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [About credits](v0.9.0-about-credits.png) | 800×660 | 2026-10-21T19:04:44+00:00 | `6e701e1d95429f8b028f292284fbf1fa42189364aaa59552e238a0b616e66d8b` |
| [About offline licenses](v0.9.0-about-licenses.png) | 800×660 | 2026-10-21T19:04:44+00:00 | `bfbe37ffaaff095610fd4952f47b6f842d24baf572299ead4a1ed732d7eb721d` |
| [About and support](v0.9.0-about.png) | 800×660 | 2026-10-21T19:04:44+00:00 | `c2f0c64dad76a960edc230fe3f6b64a9e0ecba47196cca042b57415538a6652c` |
| [Archive search and saved media](v0.9.0-archive.png) | 1440×920 | 2026-10-21T19:04:42+00:00 | `189cce7590b31bb1de0c4d0a427b882ef62b0e54a470d62b9b9064f8c8134a8c` |
| [Downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-21T19:04:38+00:00 | `c5530e591146906348b6c025e47162911af6f2b350d0fafd7e57f89fa207d953` |
| [Repeated HTML export](v0.9.0-export.png) | 1000×820 | 2026-10-21T19:04:43+00:00 | `25b9f60a71c95e98a7e8fb8f3d5222519c27d3c30db87ab13dccb28677cf0fe1` |
| [Favorite original-export dialog](v0.9.0-favorites-original-export.png) | 600×300 | 2026-10-21T19:04:43+00:00 | `f65ba40ddbc480a35883cb33903d44af5b9f356478fbf889417604c2dc0ef165` |
| [Favorite original-export result](v0.9.0-favorites-original-result.png) | 600×300 | 2026-10-21T19:04:43+00:00 | `ffdc94cd7e8bc19e9bd859d105c9c9c321ae440edf49c48816c1876f0ecfccaa` |
| [Favorites overview](v0.9.0-favorites.png) | 760×580 | 2026-10-21T19:04:43+00:00 | `9c03edc0c05b973b16a7e3405c47644435c2d40e442bf4a284415a7204de9567` |
| [Gallery with saved-post context](v0.9.0-gallery.png) | 1440×920 | 2026-10-21T19:04:40+00:00 | `a8e6f92fba1dddc8096e068566c18b626b14ccf2344b735a37c31bf1a4d95eae` |
| [Reader with expanded replies](v0.9.0-reader-replies.png) | 1440×960 | 2026-10-21T19:04:41+00:00 | `e70e7769f25b4ecbbf89567e5e0c7e67c672148597600a0950f9d67a9e636f42` |
| [Offline reader](v0.9.0-reader.png) | 1440×960 | 2026-10-21T19:04:41+00:00 | `f2705fce2dd7920c849a84835b40a26dc1959071d4fe393634fd5e3c9451feec` |
| [Rules and effective values](v0.9.0-rules.png) | 1200×700 | 2026-10-21T19:04:44+00:00 | `821d3897b379fb908a2aeee361c0b69f7cdcb23829c0be77b629ea5f64f80a16` |
| [Settings and reading preferences](v0.9.0-settings.png) | 1200×700 | 2026-10-21T19:04:44+00:00 | `667c2d92a163ee90ed55512d41d60e6a63ab0f7a6da73bc9cce4573c3b886b90` |
