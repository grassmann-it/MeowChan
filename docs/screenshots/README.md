# MeowChan 0.9.0 screenshot provenance

These five PNGs are unedited, byte-identical window renders from the real integrated MeowChan interface. They were captured on 2026-10-20 from the same runtime and capture sources committed as `c979cb7d5c1887965d30039aa4283991eb7d972a`. The capture-only correction makes the first synthetic 404 retry due after 15 minutes; production UI/runtime behavior is unchanged. Subsequent documentation/package changes do not alter the captured interface. Source: [showcase capture code](https://github.com/tfgrass/MeowChan/blob/c979cb7d5c1887965d30039aa4283991eb7d972a/tests/MeowChan.DesktopSmoke/ShowcaseCaptures.cs) and [demo-data construction](https://github.com/tfgrass/MeowChan/blob/c979cb7d5c1887965d30039aa4283991eb7d972a/tests/MeowChan.DesktopSmoke/ShowcaseData.cs).

The host was Manjaro Linux (rolling), using a private 3200×2000 Xvfb display, Openbox, a private DBus session/runtime and software rendering. The requested and measured RenderScaling were both **1.0 (100%)**. The main scene uses Simple, Dark and English. Captures exclude native window decorations; there is no crop, compositing or fabricated interface text.

All artwork is original deterministic landscape drawing code using Skia in `ShowcaseData`; posts, identities and download states are synthetic demonstration data. There are 56 synthetic retained threads and eight original landscape images. No external/private content, downloaded artwork or generative-image assets were used. Downloads projects passive example states rather than starting watches; Archive, gallery, post context and repeated HTML export use actual persisted local content and production services. The seeded HTTP handler rejects network requests. The same existing MeowChan cat asset appears in the application and About.

The showcase passed, and all five final renders were visually inspected before promotion. This is source-built UI evidence, separate from native workflows in freshly extracted release packages. It does not establish live source availability, GPU rendering, native tray, accessibility or global-menu integration. The isolated log records expected missing session/accessibility/global-menu services as warnings; it finishes with normal window close, released profile lease and PASS without binding errors.

## Reproduce

Build the harness from the identified source with the pinned SDK and reviewed dependencies, then run on Linux with Xvfb, Openbox, DBus and their native libraries available. This invocation used locally extracted GUI prerequisites:

```sh
MEOWCHAN_GUI_TOOLS="$PWD/artifacts/meowchan-rename-validation/gui-tools/root/usr" \
PATH="$PWD/artifacts/meowchan-rename-validation/gui-tools/root/usr/bin:$PATH" \
LD_LIBRARY_PATH="$PWD/artifacts/meowchan-rename-validation/gui-tools/root/usr/lib" \
XDG_DATA_DIRS="$PWD/artifacts/meowchan-rename-validation/gui-tools/root/usr/share" \
python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --output artifacts/r09-showcase-v5 --mode visual --scale 1 --timeout 180
```

With these prerequisites installed normally, omit the four environment overrides. Use a fresh output directory. See the [harness instructions](https://github.com/tfgrass/MeowChan/blob/c979cb7d5c1887965d30039aa4283991eb7d972a/tests/MeowChan.DesktopSmoke/README.md).

Local retained evidence is `artifacts/r09-showcase-v5/results.json` and `artifacts/r09-showcase-v5/visual/output.log`; it is not part of the desktop archive. The log records each PNG's filename, bounds and scale. The per-image UTC times below are the original capture files' modification times, preserved in the evidence directory. Log clock times are Europe/Berlin (UTC+02:00). No independent per-image timestamp was written by the capture API.

## Original files

| Image | Pixels | Capture file time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-20T19:00:31.488+00:00 | `44d9e66747a1acf269a7be49768cae7bed9961267f371a5a9bbc502a84a3a497` |
| [gallery](v0.9.0-gallery.png) | 1440×920 | 2026-10-20T19:00:33.459+00:00 | `31806ef756c0df11cd77d54e307e02071bb099bcd8390367b174efa2479d8714` |
| [archive](v0.9.0-archive.png) | 1440×920 | 2026-10-20T19:00:34.252+00:00 | `68df59adc3988c218f2f4df747422783ed03e7255b11270e79a88b44ebd5a34f` |
| [export](v0.9.0-export.png) | 1000×820 | 2026-10-20T19:00:35.502+00:00 | `73fa393650917a59606ff410f7f3af2acd83a911463214df417f2257cef907e6` |
| [about](v0.9.0-about.png) | 560×570 | 2026-10-20T19:00:35.918+00:00 | `aecdb9352277c1b9f0fd59042185137655df8a9f4458f4fed8f12d95797caa44` |

Downloads, gallery and archive occupy the 1440×920 work area. The compact repeat-export scene is 1000×820, and About is 560×570. Each image appears under the same filename in the capture log.
