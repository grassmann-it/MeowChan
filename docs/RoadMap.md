# Roadmap

The current target is **0.9.0**, replacing the desktop interface on top of the download and session foundation released in 0.8.0. The app remains cross-platform, and `libochan` remains independently consumable by other .NET applications. 0.9.0 is in preparation; its final native package acceptance is still open.

## Existing download and library foundation

- One session owns watches, cancellation and a shared, bounded download queue. Duplicate admissions and late callbacks cannot create overlapping watch lifetimes.
- Verified media files are published atomically with manifests. Existing files, interrupted writes and restore failures have explicit recovery behavior.
- Built-in 4chan and 8kun providers share request pacing, backoff and bounded deadlines. Source availability and media completion are separate states.
- Confirmed session changes persist without depending on a graceful application exit. Settings apply through the active session, with assigned destinations preserved.
- `libochan` has its own project/package and a package-only custom-provider sample. Typed snapshots and bounded observer notifications support clients without Avalonia.
- Desktop input, settings, startup errors, shutdown and tray fallback use the same session behavior.

The [session API](Registry.md), [download pipeline](Downloader.md), and [library sample](../samples/LibOChan.Consumer/README.md) describe the actual interfaces.

## Current desktop scope

- Simple, Advanced and compact Barebones views share one live session. Switching presentation preserves active work, selection and input.
- Dark/Light, English/German, optional data-only language packs and normal/compact density are saved preferences. Simple, Dark and English are the defaults.
- Thread and board details expose real source state, assigned paths, current errors and media counts. Small windows retain access to intake, lists and details.
- Comma/newline URL lists use the existing admission path, with per-entry results and editable failed input. Single/all URL copying produces re-importable lists.
- Existing download settings, clipboard/tray actions, profile recovery and application shutdown remain part of the replacement.

The [README](../README.md) documents current controls and the [release-notes draft](releases/v0.9.0.md) records compatibility and deliberate behavior changes.

## Remaining 0.9.0 release acceptance

Acceptance requires the current core/desktop tests, native graphical harness, isolated library consumer and package checks to pass for the integrated UI. It also includes all three views, both themes, English/German, keyboard/focus behavior, small windows and actual HiDPI rendering. Recorded 0.8.0 results remain historical evidence.

Final acceptance requires evidence from the actual extracted apphost on each promised OS/CPU, including download, cancellation, restart/restore and native clipboard/folder/tray interactions. macOS candidates use an unsigned `.app` bundle inside tar.gz; native launch and Gatekeeper behavior must be recorded. Final release metadata, screenshots and artifact hashes must identify the accepted version and source commit.

The [platform and release record](platform-support.md) lists the candidate matrix, upstream requirements and the distinction between source-built harness tests and packaged executable tests. Native Windows/macOS evidence is unavailable on the current Linux development host. Cross-publish success does not close those gates.

## Later work

1.0 is reserved for a later agreed scope; this UI replacement does not choose that feature set. Pause/resume, archive/reader views, rules, import/export, thumbnails and additional convenience features remain separate work. TUI, daemon/client connections, a terminal board browser and dynamic plugin loading are longer-term extensions. Providers can already be supplied explicitly through the library contracts.
