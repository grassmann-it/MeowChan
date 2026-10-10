# Changelog

## 0.9.0 (unreleased)

This update replaces the desktop interface while keeping the 4chan/8kun download engine, durable sessions and independently usable `libochan` library.

### What's new

- **New cat icon.** The selected photographic cat-head cutout now identifies the app, its windows and tray across the desktop packages.
- **Three desktop views.** Simple is the default. Advanced adds separate thread-ID, queued-media and active-media columns. Barebones provides a smaller, denser window. Switching views preserves the running session, selection and input.
- **Downloads and Settings pages.** Both remain in the same shell, with navigation at the bottom. Switching pages retains URL input, selection, details, invalid settings drafts and running downloads. Save and Cancel stay on Settings; About remains in the application menu.
- **Compact workspace.** The footer selector lists Barebones, Simple and Advanced. Grouped table cells and initially closed details give downloads more space; import results expand within the intake panel. Menu icons adapt to their theme. Shared button states, readable primary labels, keyboard focus and aligned Settings actions follow the same Dark/Light styling.
- **Appearance and language preferences.** Dark/Light themes, normal/compact density and built-in English/German are saved in the profile. Optional data-only JSON language packs load at startup, with English fallback and visible warnings for invalid or missing packs.
- **Batch URL admission.** Add or Enter accepts comma- and newline-separated thread/board URLs through the existing provider registry. Each entry receives a result, including duplicates and failures; valid later entries still run. Failed lists remain editable, and edits made during an import are preserved.
- **Details for threads and boards.** A side panel, or an overlay in narrow windows, shows the selected URL, actual assigned folder, status and current errors. Thread details also show saved/known media and queued/active media counts. Folder, browser, copy and remove actions remain accessible.
- **Reusable URL copying.** Copy a single URL or all watched thread and board URLs as a newline-separated list. Empty Add, the tray action and the main-window paste shortcut import the clipboard once when explicitly invoked.
- **Updated desktop dialogs.** About and startup errors use the new appearance and localization while retaining profile/tray recovery paths.
- **Linux tray lifecycle fix.** Avalonia 12.1.4 includes the upstream correction for asynchronous registration continuing after tray disposal. Matching Actipro/DataGrid packages, native graphics dependencies, package locks and notices are updated.

### Upgrade and behavior notes

- Existing profiles, watched URLs and assigned download paths remain usable. New appearance fields default to **Simple, Dark, English and normal density** when absent. Main-window theme/view changes save immediately; pending Settings changes require a successful **Save**. Cancel reloads confirmed settings; neither action leaves the page.
- **Double-click opens details**, replacing the previous folder-opening shortcut. Open folder remains a separate action. Escape closes details; Ctrl+L/Cmd+L switches to Downloads and focuses URL input, and Shift+Enter inserts a newline.
- Progress is **saved / selected media**, with rule exclusions shown separately; it is not bytes or a final thread size. Monitoring continues at 100%. “Archived” describes the source, not a local HTML archive. The import report describes the latest import, not a persistent history.
- View/theme/language/density preferences persist. Window geometry, column layout and details-panel width do not persist across restarts.
- .NET 10 and the existing platform targets are retained. This UI update introduces no new public `libochan` API requirement.

### Release status

0.9.0 is in preparation. Final native acceptance of freshly extracted packages remains open; the existing 0.8.0 CI evidence does not validate the new interface. macOS bundles remain unsigned and not notarized. See the [0.9.0 release-notes draft](docs/releases/v0.9.0.md) and [platform validation record](docs/platform-support.md).

### Additional work on the current branch

The branch now also contains live Diagnostics, a persistent Archive with an offline post/media reader, and basic inherited media rules. Publishing and final version allocation remain separate decisions.

- Media admission is bounded (256 pending reservations by default), cancellable and fair between manual and board-discovered work; revision successors share that pending budget. Board origins survive restart, and explicit URL admission promotes a tracked thread to manual priority.
- Sources shows registered adapter capabilities and actual watch observations, with shared URL admission, canonical source rules and exact-watch navigation. Registration does not indicate site availability; external providers may optionally declare capabilities without an interface-breaking change.
- Diagnostics shows current watch failures, request/cooldown state, content-storage failures and session warnings, with copy and exact-watch navigation.
- The local SQLite catalog retains observed posts for 4chan/8kun. Media may remain on NAS drives; slow media discovery does not block local reading. Removing monitoring preserves captured content. New watches capture posts; restored older watches start media-only until explicitly enabled.
- Rules resolve allowed extensions and maximum metadata file size through Global → Source → Board → Thread, with visible origins and durable first decisions. Existing decisions and files survive later changes. Unknown sizes remain allowed; this is not a transfer-byte cap.
- Manifests retain saved media versions, and the independent library reader verifies selected bytes. Changed size/hash during an active download schedules the latest revision afterward.

Progress now uses **saved / selected media**, with exclusions shown separately. The additive `ThreadContentRuntime`, `ArchiveReader`, rules and media-reader APIs remain independent of Avalonia. Back up the profile and media manifests before upgrading: v1 manifests are accepted, but the new v2 history is not a downgrade format for older binaries.

Sources is concretely planned but has no product page yet. Pause/resume, full-text search, import/export, TUI/daemon clients and dynamic plugin loading remain later work.

## 0.8.0

This update rebuilds download and session handling for 4chan and 8kun and makes `libochan` usable independently of the desktop app.

### What's new

- **More reliable source checks.** Updated 8kun media URLs, canonical URL handling and conditional thread requests reduce broken or duplicate work. Archived or missing sources remain distinguishable from unfinished media downloads.
- **Controlled HTTP requests.** Shared request pacing, bounded retries, server cooldowns and request/body deadlines handle rate limits and temporary failures. Stopping a watch cancels its requests and waits for cleanup.
- **Verified downloads.** Size and checksum checks, atomic file publication and manifests prevent partial files from appearing complete. Existing unrelated files are preserved. Rechecks retry failed media and repair missing or changed files even when the thread response is unchanged; cached verification reduces repeated disk reads.
- **One shared download queue.** Parallel-download and bandwidth limits apply across all watched threads. Duplicate jobs are suppressed, and removal waits for the affected downloads to stop. Removing a board leaves its admitted threads running; removing a thread prevents automatic rediscovery until it is explicitly added again.
- **Durable sessions and settings.** With session restore enabled, watch changes are saved as they happen. Saved sessions retain removed-thread suppression and assigned download roots. Settings are validated before use, save failures are visible, and recovery preserves damaged files. Changing the output root keeps existing watches in their assigned directories.
- **Desktop fixes.** Thread rows show source state, media progress and queued/active counts. Sorting handles numeric values correctly, context actions target the clicked row, and folder actions open the assigned destination. Clipboard import, settings dialogs and tray fallback are more reliable; Exit remains accessible without a working tray.
- **Standalone library.** `libochan` is a separate library project with local NuGet packaging, without Avalonia or desktop configuration. It provides explicit provider registration, asynchronous session control, typed snapshots and isolated state notifications. Logging is optional and silent by default. A package consumer sample verifies download and reuse through public APIs.

### Upgrade notes

- **.NET 10 replaces .NET 8.** Source builds use the SDK pinned in `global.json`; library consumers require .NET 10. Self-contained desktop packages include the runtime.
- **Download paths now separate providers:** `<root>/<source>/<board>/<thread>/<media ID>.<extension>`. The default root is now `Downloads/oChan` under the user home directory; existing settings retain their configured root. Legacy files discovered through current provider metadata are imported only when size and MD5 match, leaving the originals untouched. Old completion markers alone are not trusted; interrupted transfers restart from the beginning.
- **Profiles keep their existing locations:** `%APPDATA%/.oChan` on Windows and `$HOME/.oChan` on macOS/Linux. Older saved URL lists are accepted when session restore is enabled. Only one application instance may own a profile; use an absolute `OCHAN_PROFILE` directory for a separate instance.
- **Library integration changes are breaking.** Source integrations must adopt explicit dependencies, asynchronous session operations and snapshot notifications. Desktop configuration is no longer part of the library. See the [session API](docs/Registry.md) and [package consumer sample](samples/LibOChan.Consumer/README.md).

### Packages and validation

Packaging targets Windows x64, Linux x64/Arm64 and macOS Intel/Apple Silicon. Release builds use a pinned SDK and reviewed dependency locks, with expanded offline tests, graphical checks and package validation. Extract the complete archive, including its native libraries and notices.

Final native acceptance of the extracted packages is still outstanding across the target matrix. macOS bundles are unsigned and not notarized; native launch and Gatekeeper validation remain open. Linux requires glibc and an X11 or XWayland desktop. The [platform validation record](docs/platform-support.md) lists prerequisites and the evidence available for each target.
