# MeowChan 0.9.0 screenshot provenance

These six PNGs are unedited, byte-identical window renders from the real integrated MeowChan interface. They were captured on 2026-10-20 from production UI/runtime and capture harness committed as `8d31f2c7050f3e33bc64aeba941ddd2a093fe143`. The build included subsequent package-copy declarations in `MeowChan.csproj`; these only add published documentation/image files and do not change the captured interface. Source: [showcase capture code](https://github.com/tfgrass/MeowChan/blob/8d31f2c7050f3e33bc64aeba941ddd2a093fe143/tests/MeowChan.DesktopSmoke/ShowcaseCaptures.cs) and [demo-data construction](https://github.com/tfgrass/MeowChan/blob/8d31f2c7050f3e33bc64aeba941ddd2a093fe143/tests/MeowChan.DesktopSmoke/ShowcaseData.cs). Exact source-input and binary hashes were retained with the local run evidence.

The host was Manjaro Linux (rolling), using a private 3200×2000 Xvfb display, Openbox, a private DBus session/runtime and software rendering. Requested and measured RenderScaling were both **1.0 (100%)**. The main style is Simple, Dark and English. Captures exclude native window decorations; there is no crop, compositing, or fabricated interface text.

All artwork is original deterministic drawing code using Skia in `ShowcaseData`; posts, identities and download states are synthetic demonstration data. There are 56 synthetic retained threads, 173 posts with saved attachments, and eight original image compositions: a 1600×720 opening panorama, a 1000×1500 forest portrait and six 1600×1000 landscapes. No external/private content, downloaded artwork or generative-image assets were used. Downloads projects passive example states rather than starting watches. Its selected row uses the real work formatter with 17 succeeded media and one first-attempt HTTP 404 waiting until 15 minutes after the recorded attempt; the thread source remains active. Archive, gallery, full-window reader, post context and repeated HTML export use actual persisted local content and production services. The seeded HTTP handler rejects network requests. The same existing MeowChan cat asset appears in the application header and About.

The isolated Release build completed with zero warnings/errors. The six-scene showcase passed, and all six renders were visually inspected by both the capture worker and root reviewer before byte-identical promotion. The Reader scene requires a large decoded image, readable opening post text and visible post/thread navigation; the Archive scene waits for all eight decoded thumbnails in multiple rows without horizontal overflow. This is source-built UI evidence, separate from native workflows in freshly extracted release packages. It does not establish live source availability, GPU rendering, native tray, accessibility, global-menu integration, or native packaged video playback. The isolated log records expected missing session/accessibility/global-menu services as warnings; it finishes with normal window close, released profile lease and PASS without binding errors.

## Reproduce

Build the harness from the identified source with the pinned SDK and reviewed dependencies, then run on Linux with Xvfb, Openbox, DBus and their native libraries available. This invocation used locally extracted GUI prerequisites and a separate build output:

```sh
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  -c Release --artifacts-path artifacts/r09-showcase-final-build \
  -m:1 -p:UseSharedCompilation=false

PATH="$PWD/artifacts/gui-test-tools/bin:$PWD/artifacts/gui-test-tools/usr/bin:$PATH" \
LD_LIBRARY_PATH="$PWD/artifacts/gui-test-tools/usr/lib" \
XDG_DATA_DIRS="$PWD/artifacts/gui-test-tools/usr/share" \
python3 scripts/run-desktop-smoke.py \
  --assembly artifacts/r09-showcase-final-build/bin/MeowChan.DesktopSmoke/release/MeowChan.DesktopSmoke.dll \
  --output artifacts/r09-showcase-final --mode visual --scale 1 --timeout 180
```

With these prerequisites installed normally, omit the three environment overrides. Use a fresh output directory and allow the harness to create its own fresh `/tmp/MeowChan-showcase` base; never reuse a personal profile. The export folder is a safe deterministic sibling of the isolated profile. See the [harness instructions](https://github.com/tfgrass/MeowChan/blob/8d31f2c7050f3e33bc64aeba941ddd2a093fe143/tests/MeowChan.DesktopSmoke/README.md).

Local retained evidence is `artifacts/r09-showcase-final/build.log`, `results.json`, `visual/output.log`, `source-manifest.json` and `capture-manifest.json`; it is not part of the desktop archive. No source `.cs`/`.axaml` input changed between the recorded build manifest and capture verification. The log records each PNG's filename, bounds and scale. Per-image UTC times below are the original capture files' modification times, preserved during promotion. Log clock times are Europe/Berlin (UTC+02:00). No independent per-image timestamp was written by the capture API.

## Original files

| Image | Pixels | Capture file time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-20T20:37:25.639350+00:00 | `cd0663bc9ed0834cc34051ee365b7da4a27930e03bad815f679b4d68a6a163d0` |
| [gallery](v0.9.0-gallery.png) | 1440×920 | 2026-10-20T20:37:26.098340+00:00 | `f603483156f8a9ffe46504cd6ba5d388428d49c291828406d32c269d20db12dc` |
| [reader](v0.9.0-reader.png) | 1440×920 | 2026-10-20T20:37:26.416332+00:00 | `be03cbc4ca1aa01229e9930acfab893bbf0007fd3b83a93b24c116b4bf22461d` |
| [archive](v0.9.0-archive.png) | 1440×920 | 2026-10-20T20:37:26.720325+00:00 | `ad63f91342ab9dc3cd1961eab39840249c9b366730ad7ba771b288cca6ac2116` |
| [export](v0.9.0-export.png) | 1000×820 | 2026-10-20T20:37:27.071317+00:00 | `ae93397b00fc57f9598909a2f5cf9661739348afac11d005444d8c7f861b0f55` |
| [about](v0.9.0-about.png) | 560×570 | 2026-10-20T20:37:27.190315+00:00 | `aecdb9352277c1b9f0fd59042185137655df8a9f4458f4fed8f12d95797caa44` |

Downloads, gallery, reader and archive use the 1440×920 client area. The compact repeat-export scene is 1000×820, and About is 560×570. Each image appears under the same filename in the capture log.
