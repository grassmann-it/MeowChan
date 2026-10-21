# Roadmap

MeowChan 0.9.0 replaces the desktop interface on top of the download and session foundation released in 0.8.0. The app remains cross-platform, and `libmeowchan` remains independently consumable by other .NET applications. The product scope below is implemented; the [platform record](platform-support.md) tracks the native checks and exact package evidence separately.

## Existing download and library foundation

- One session owns watches, cancellation and a shared, bounded download queue. Duplicate admissions and late callbacks cannot create overlapping watch lifetimes.
- Verified media files are published atomically with manifests. Existing files, interrupted writes and restore failures have explicit recovery behavior.
- Built-in 4chan and 8kun providers share request pacing, backoff and bounded deadlines. Source availability and media completion are separate states.
- Confirmed session changes persist without depending on a graceful application exit. Settings apply through the active session, with assigned destinations preserved.
- `libmeowchan` has its own project/package and a package-only custom-provider sample. Typed snapshots and bounded observer notifications support clients without Avalonia.
- Desktop input, settings, startup errors, shutdown and tray fallback use the same session behavior.

The [session API](Registry.md), [download pipeline](Downloader.md), and [library sample](../samples/LibMeowChan.Consumer/README.md) describe the actual interfaces.

## 0.9.0 desktop scope

- Barebones, Simple and Advanced views share one live session. Switching presentation preserves active work, selection and input. Each view keeps its own table columns, archive layout, density and reading size.
- Dark/Light/System, English/German, optional data-only language packs and normal/compact density are saved preferences. Barebones, Dark, English and normal density are the defaults for new profiles.
- Thread and board details expose real source state, assigned paths, current errors and media counts. Small windows retain access to intake, lists and details.
- Comma/newline URL lists use the existing admission path, with per-entry results and editable failed input. Single/all URL copying produces re-importable lists.
- Existing download settings, clipboard/tray actions, profile recovery and application shutdown remain part of the replacement.

The [user guide](Help.md) documents package startup, current controls and offline export; the [release notes](releases/v0.9.0.md) record compatibility and deliberate behavior changes.

## Archive and completion in 0.9.0

- Local post capture, indexed search, quotes/backlinks and inherited media rules accompany the download session. Removing a watch preserves captured content and saved media.
- Thread row/card double-click opens the large reader; Shift+Enter or the context menu opens the shared gallery. Saved-media rows/tiles enter the gallery at the chosen item. Images of results freezes all found threads across result pages in current sort order. Media entry follows its visible sort/version filter.
- Reader and gallery retain list/search origins; leaving the gallery returns to the saved post associated with its current media when available. Gallery provides image navigation, Fit/1:1, zoom/pan, saved-post context and local video playback. F11 toggles fullscreen across the workspace, reader and gallery. PNG/JPEG/GIF/WebP/BMP previews remain bounded; animations show a first frame. See [limits](Help.md#images-search-and-navigation).
- Durable media 404 pauses follow 15/30/60/120/240 minutes, then at least 240. Terminal sources have a fixed 12-hour follow-up; completion and missing-media outcomes remain separate. Manual retry grants one outer attempt and does not restart the source-end window.
- Single-thread and combined multi-thread HTML export recognize previous exports and offer explicit update or a new folder, preserving a backup for changes/reduced output. The result folder opens directly after success.
- The desktop supplies a leased SQLite work journal; library clients choose durable `WorkStateStore` explicitly. The default library work state is in memory.

## Native release gates

The [platform record](platform-support.md) distinguishes completed source and native checks from work still pending on the final product and package revision. Results from 0.8.0 and earlier 0.9.0 checkpoints remain historical evidence.

Final package acceptance must identify the source commit and artifact hashes for Windows x64 ZIP, Intel/Apple Silicon macOS DMGs, and the Linux x64/ARM64 tar and AppImage choices. macOS DMGs contain ad-hoc-signed apps and an Applications shortcut, without Developer ID signing or notarization. Native workflow and host-integration evidence belongs to the exact delivered packages; physical-device and Gatekeeper limits should be recorded accurately.

The [platform and release record](platform-support.md) lists upstream requirements, native results and the distinction between source-built harness tests and packaged executable tests. Cross-publish success alone does not close those gates.

## Later work

The agreed 1.0 focus moves source integration forward; its wider feature set is not
settled by 0.9.0. Structured import/export, OCR, new host/API or
NAS services, TUI/daemon clients and dynamic plugin loading remain later work.
Providers can already be supplied explicitly through the library contracts.
