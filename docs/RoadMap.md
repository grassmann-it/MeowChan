# Roadmap

The current target is **0.9.0**, replacing the desktop interface on top of the download and session foundation released in 0.8.0. The app remains cross-platform, and `libmeowchan` remains independently consumable by other .NET applications. 0.9.0 is in preparation; its final native package acceptance is still open.

## Existing download and library foundation

- One session owns watches, cancellation and a shared, bounded download queue. Duplicate admissions and late callbacks cannot create overlapping watch lifetimes.
- Verified media files are published atomically with manifests. Existing files, interrupted writes and restore failures have explicit recovery behavior.
- Built-in 4chan and 8kun providers share request pacing, backoff and bounded deadlines. Source availability and media completion are separate states.
- Confirmed session changes persist without depending on a graceful application exit. Settings apply through the active session, with assigned destinations preserved.
- `libmeowchan` has its own project/package and a package-only custom-provider sample. Typed snapshots and bounded observer notifications support clients without Avalonia.
- Desktop input, settings, startup errors, shutdown and tray fallback use the same session behavior.

The [session API](Registry.md), [download pipeline](Downloader.md), and [library sample](../samples/LibMeowChan.Consumer/README.md) describe the actual interfaces.

## Current desktop scope

- Simple, Advanced and compact Barebones views share one live session. Switching presentation preserves active work, selection and input.
- Dark/Light, English/German, optional data-only language packs and normal/compact density are saved preferences. Simple, Dark and English are the defaults.
- Thread and board details expose real source state, assigned paths, current errors and media counts. Small windows retain access to intake, lists and details.
- Comma/newline URL lists use the existing admission path, with per-entry results and editable failed input. Single/all URL copying produces re-importable lists.
- Existing download settings, clipboard/tray actions, profile recovery and application shutdown remain part of the replacement.

The [user guide](Help.md) documents package startup, current controls and offline export and the [release-notes draft](releases/v0.9.0.md) records compatibility and deliberate behavior changes.

## Archive and completion in 0.9.0

- Local post capture, indexed search, quotes/backlinks and inherited media rules accompany the download session. Removing a watch preserves captured content and saved media.
- Thread cards/tables and saved-media rows/tiles enter the shared gallery. Thread double-click opens that thread; Images of results freezes all found threads across result pages in current sort order. Media entry follows its visible sort/version filter. Context menus keep reading and HTML export directly reachable.
- Gallery provides arrows, Fit/1:1, zoom/pan and saved-post context. PNG/JPEG/GIF/WebP/BMP previews remain bounded; animations show a first frame. See [limits](Help.md#images-search-and-navigation).
- Durable media 404 pauses follow 15/30/60/120/240 minutes, then at least 240. Terminal sources have a fixed 12-hour follow-up; completion and missing-media outcomes remain separate. Manual retry grants one outer attempt and does not restart the source-end window.
- Compact single-thread HTML export recognizes previous exports and offers explicit update or a new folder, preserving a backup for changes/reduced output. The result folder opens directly after success.
- The desktop supplies a leased SQLite work journal; library clients choose durable `WorkStateStore` explicitly. The default library work state is in memory.

## Remaining 0.9.0 release acceptance

Acceptance requires the current core/desktop tests, native graphical harness, isolated library consumer and package checks to pass for the integrated UI. It also includes all three views, both themes, English/German, keyboard/focus behavior, small windows and actual HiDPI rendering. Recorded 0.8.0 results remain historical evidence.

Final acceptance requires evidence from the actual extracted apphost on each promised OS/CPU, including download, cancellation, restart/restore and native clipboard/folder/tray interactions. macOS candidates use an unsigned `.app` bundle inside tar.gz; native launch and Gatekeeper behavior must be recorded. Final release metadata, screenshots and artifact hashes must identify the accepted version and source commit.

The [platform and release record](platform-support.md) lists the candidate matrix, upstream requirements and the distinction between source-built harness tests and packaged executable tests. Native Windows/macOS evidence is unavailable on the current Linux development host. Cross-publish success does not close those gates.

## Later work

The agreed 1.0 focus moves source integration forward; its wider feature set is not
settled by 0.9.0. Collection exports, structured import/export, OCR, new host/API or
NAS services, TUI/daemon clients and dynamic plugin loading remain later work.
Providers can already be supplied explicitly through the library contracts.
