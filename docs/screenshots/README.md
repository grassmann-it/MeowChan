# MeowChan 0.9.0 screenshot provenance

These six PNGs are unedited, byte-identical window renders from the real integrated MeowChan interface. They were captured on 2026-10-21 from the clean production UI/runtime and capture harness committed as `becd9b38b7df0237bc6115a9cd75532b79115bc8`. Source: [showcase capture code](https://github.com/tfgrass/MeowChan/blob/becd9b38b7df0237bc6115a9cd75532b79115bc8/tests/MeowChan.DesktopSmoke/ShowcaseCaptures.cs) and [demo-data construction](https://github.com/tfgrass/MeowChan/blob/becd9b38b7df0237bc6115a9cd75532b79115bc8/tests/MeowChan.DesktopSmoke/ShowcaseData.cs). Exact source-input, binary and PDB hashes were retained with the local run evidence.

The host was Manjaro Linux (rolling), using a private 3200×2000 Xvfb display, Openbox, a private DBus session/runtime and software rendering. Requested and measured RenderScaling were both **1.0 (100%)**. The main style is Simple, Dark and English. Captures exclude native window decorations; there is no crop, compositing, or fabricated interface text.

All artwork is original deterministic drawing code using Skia in `ShowcaseData`; posts, identities and download states are synthetic demonstration data. There are 56 synthetic retained threads, 173 posts with saved attachments, and eight original image compositions: a 1600×720 opening panorama, a 1000×1500 forest portrait and six 1600×1000 landscapes. No external/private content, downloaded artwork or generative-image assets were used. Downloads projects passive example states rather than starting watches. Its selected row uses the real work formatter with 17 succeeded media and one first-attempt HTTP 404 waiting until 15 minutes after the recorded attempt; the thread source remains active. Archive, gallery, full-window reader, post context and repeated HTML export use actual persisted local content and production services. The seeded HTTP handler rejects network requests. The same existing MeowChan cat asset appears in the application header and About.

The Release build completed with zero warnings/errors (4.26 seconds). The six-scene showcase passed, and all six renders were visually inspected by both the capture worker and root reviewer before byte-identical promotion. The Reader scene requires a large decoded image and the complete opening post body inside the actual posts scroll viewport, with visible post/thread navigation; the Archive scene waits for all eight decoded thumbnails in multiple rows without horizontal overflow. This is source-built UI evidence, separate from native workflows in freshly extracted release packages. It does not establish live source availability, GPU rendering, native tray, accessibility, global-menu integration, or native packaged video playback. The isolated log records expected missing session/accessibility/global-menu services as warnings; it finishes with normal window close, released profile lease and PASS without binding errors.

## Reproduce

Build the harness from the identified source with the pinned SDK and reviewed dependencies, then run on Linux with Xvfb, Openbox, DBus and their native libraries available. This invocation used locally extracted GUI prerequisites and the existing final Release build output:

```sh
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  -c Release --no-restore -v quiet

PATH="$PWD/artifacts/gui-test-tools/bin:$PWD/artifacts/gui-test-tools/usr/bin:$PATH" \
LD_LIBRARY_PATH="$PWD/artifacts/gui-test-tools/usr/lib" \
XDG_DATA_DIRS="$PWD/artifacts/gui-test-tools/usr/share" \
python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll \
  --output artifacts/r09-showcase-overlay-final --mode visual --scale 1 --timeout 180
```

With these prerequisites installed normally, omit the three environment overrides. Use a fresh output directory and allow the harness to create its own fresh `/tmp/MeowChan-showcase` base; never reuse a personal profile. The export folder is a safe deterministic sibling of the isolated profile. See the [harness instructions](https://github.com/tfgrass/MeowChan/blob/becd9b38b7df0237bc6115a9cd75532b79115bc8/tests/MeowChan.DesktopSmoke/README.md).

Local retained capture evidence is `artifacts/r09-showcase-overlay-final/results.json`, `visual/output.log`, `source-manifest.json`, `source-diff.patch` and `capture-manifest.json`; it is not part of the desktop archive. The source manifest records 258 source inputs, a clean Git status at capture, and the final binaries/PDBs. All recorded source and binary hashes were verified unchanged after capture. Build success was recorded in tool session 22461; no separate build log file was retained. The same DLLs passed tray-host, no-tray and tray-failure checks in `artifacts/tray-split-freeze-20261021`.

The captured `MeowChan.dll` SHA-256 is `f0affbb491463a1b41fa2c7e47e3ebe61fb99cdcd5d3ccb30332d14d46ef2039`; `MeowChan.DesktopSmoke.dll` is `a7e9ca821dbd4d864a6923d2c48829b76d3f8661fa58a0b5c548d82458824f73`; `libmeowchan.dll` is `c52a26e72f10622060f333b3c50e800b64b9ae4de230bde6bfef18de734b352b`. The source-manifest file SHA-256 is `f36f5484a90475bd276d1cea1e65e42f4ff9de0835e07eac7142f0b6282ebe15`.

The log records each PNG's filename, bounds and scale. Per-image UTC times below are the original capture files' modification times, preserved during promotion. Log clock times are Europe/Berlin (UTC+02:00). No independent per-image timestamp was written by the capture API.

## Original files

| Image | Pixels | Capture file time (UTC) | SHA-256 |
| --- | --- | --- | --- |
| [downloads](v0.9.0-downloads.png) | 1440×920 | 2026-10-21T15:06:00.585434+00:00 | `ec57a20576e0b708ac3a59cb9d8696d4e293acdf2440aa1155031ee5f4592261` |
| [gallery](v0.9.0-gallery.png) | 1440×920 | 2026-10-21T15:06:01.044413+00:00 | `e464f5c0ff3d7b25d58f3f4aef686132f2984fc30516fc6bc98e71ae8fa19078` |
| [reader](v0.9.0-reader.png) | 1440×960 | 2026-10-21T15:06:01.405396+00:00 | `33f1ff8501bcf619f33be031ebeabd88e2730582f90535a602e9c98b8cd3799d` |
| [archive](v0.9.0-archive.png) | 1440×920 | 2026-10-21T15:06:01.739380+00:00 | `cbc0d3518a5ab616aa6c64ddffd2fd74182248322c359b60e4221c52e0dbe2aa` |
| [export](v0.9.0-export.png) | 1000×820 | 2026-10-21T15:06:02.076365+00:00 | `8ad7f07e442153e081abf6a2d9cc04d3c4cbacfe49a7698a916075c872705654` |
| [about](v0.9.0-about.png) | 560×570 | 2026-10-21T15:06:02.196359+00:00 | `aecdb9352277c1b9f0fd59042185137655df8a9f4458f4fed8f12d95797caa44` |

Downloads, gallery and archive use the 1440×920 client area. Reader uses 1440×960 to show the complete opening image and all three body-text lines. The compact repeat-export scene is 1000×820, and About is 560×570. Each image appears under the same filename in the capture log.
