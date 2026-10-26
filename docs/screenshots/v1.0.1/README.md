# MeowChan 1.0.1 screenshots

Fresh application captures from the published 1.0.1 source checkpoint
`2518feb60928977f3ebf16cb656dbdf6b2060bfe`, captured on 2026-10-26.
All eight images use English, Dark theme and isolated offline demonstration data.
They are unedited PNGs from the real application UI, visually reviewed before
promotion. See [manifest.json](manifest.json) for image hashes, sizes, binary
hashes, build/capture commands and retained local evidence paths.

| Image | View |
| --- | --- |
| [Downloads](downloads.png) | Thread status and watch details |
| [Archive](archive.png) | Saved-media tiles and local search |
| [Archive table](archive-table.png) | Saved threads with aligned columns |
| [Reader](reader.png) | Captured posts and locally saved media |
| [Gallery](gallery.png) | Original artwork with post details and tools |
| [Media focus](gallery-focus.png) | Gallery with tools and details folded away |
| [Settings](settings.png) | Interface settings in the eight-page workspace |
| [Rules](rules.png) | Scoped media rule editor |

The Release build and visual smoke scenario passed, with measured RenderScaling
1.0, normal application shutdown and no binding errors. Capture conditions:
Linux, isolated 3200×2000 Xvfb display, Openbox, private DBus/runtime directories
and software rendering. Main scenes are 1440×920, the reader is 1440×960, and
Settings/Rules are 1200×700. Captures exclude native window decorations.

`ShowcaseData` supplies synthetic archived threads, posts and original procedural
Skia artwork. Downloads rows are passive demonstration projections; archive,
reader and gallery use the real local services. No live watches, HTTP requests or
personal profiles are used. These source-built captures do not establish native
release-package acceptance, hardware video playback or live-source availability.

The `v0.9.0-*` and `smoke-1.0-*` source filenames in the manifest are historical
harness names; every image in this directory was freshly rendered from the
1.0.1 source above. Earlier [1.0.0](../v1.0.0/README.md) and
[0.9.0](../README.md) captures retain their original provenance.
