# Changelog

## 0.9.0 (unreleased)

This update replaces the desktop interface while keeping the 4chan/8kun download engine, durable sessions and independently usable `libmeowchan` library.

### What's new

- **Shared local gallery.** Thread rows/cards, saved-media tables/tiles and inline images open one viewer with Left/Right, Fit/1:1, zoom/pan and saved-post context. Images of results freezes every found thread in current sort order across result pages, including nonmatching posts; saved-media navigation follows its sort/version filter. Animated images show a first frame. Preview safety limits are 64 MiB and 80 million source pixels.
- **Durable media follow-up.** Consecutive 404s pause 15/30/60/120/240 minutes, then at least 240. A terminal source gets a fixed 12-hour follow-up from its first durably recorded end. Restart/pause do not extend it; completion can retain missing 404 media. Other exhausted/capture/storage failures require intervention. Check-now respects media pauses; manual retry grants one outer attempt, including after completion, without a new watch or end window.
- **Compact repeat export.** Known export targets survive restart. Choose a new folder or explicitly update an inspected earlier export; Open folder opens the current successful result. Modified or reduced output preserves the previous folder after confirmation.

- **Check now and separate media retry.** Downloads and Diagnostics can request a check on the exact watch. Checks coalesce, obey provider limits and respect media due times. The separate media retry grants one outer attempt without changing previous rule decisions; completed-thread repair does not restart source monitoring.
- **Persistent Pause/Resume.** Header and menu stop running transfers and source requests as well as queued work; completed files remain intact, and interrupted transfers restart after Resume. Pause survives desktop restarts.

- **Local archive search and navigation.** Search retained post text, titles and identities, filter by source/board/state/capture preference and sort results. Captured quote targets open on the correct post page; local backlinks and reading-position history work across threads. Missing and external references remain explicit without remote fetching.
- **Portable HTML thread export.** Archive writes a new independent folder with captured posts, its own CSS and verified matching saved originals. Retained older media revisions remain selectable by captured checksum; missing media and preserved rule exclusions are explained. Known earlier exports can be explicitly updated or kept by choosing a new folder. Updates stage a complete new result; changed/extra files, associated legacy exports and reduced output require preserving the old folder as a backup. Media-only archives export a current-original gallery.

- **New cat icon.** The selected photographic cat-head cutout now identifies the app, its windows and tray across the desktop packages.
- **Three desktop views.** Barebones is the default, with a smaller, denser window. Simple and Advanced offer more detail. Each view keeps its own density, reading size, archive layout and table column visibility, width and order. Column menus let you choose or reset the displayed columns. Switching views preserves the running session, selection and input.
- **Downloads and Settings pages.** Both remain in the same shell, with navigation at the bottom. Switching pages retains URL input, selection, details, invalid settings drafts and running downloads. Save and Cancel stay on Settings; About opens from the application menu or the cat logo in the header.
- **Compact workspace.** The footer selector lists Barebones, Simple and Advanced. Grouped table cells and initially closed details give downloads more space; import results expand within the intake panel. Menu icons adapt to their theme. Shared button states, readable primary labels, keyboard focus and aligned Settings actions follow the same Dark/Light styling.
- **Appearance and language preferences.** Dark/Light themes, normal/compact density and built-in English/German are saved in the profile. Optional data-only JSON language packs load at startup, with English fallback and visible warnings for invalid or missing packs.
- **Batch URL admission.** Add or Enter accepts comma- and newline-separated thread/board URLs through the existing provider registry. Each entry receives a result, including duplicates and failures; valid later entries still run. Failed lists remain editable, and edits made during an import are preserved.
- **Details for threads and boards.** A side panel, or an overlay in narrow windows, shows the selected URL, actual assigned folder, status and current errors. Thread details also show saved/known media and queued/active media counts. Folder, browser, copy and remove actions remain accessible.
- **Reusable URL copying.** Copy a single URL or all watched thread and board URLs as a newline-separated list. Empty Add, the tray action and the main-window paste shortcut import the clipboard once when explicitly invoked.
- **Updated desktop dialogs.** About and startup errors use the new appearance and localization while retaining profile/tray recovery paths.
- **Linux tray lifecycle fix.** Avalonia 12.1.4 includes the upstream correction for asynchronous registration continuing after tray disposal. Matching Actipro/DataGrid packages, native graphics dependencies, package locks and notices are updated.

### Upgrade and behavior notes

- Existing profiles, watched URLs and assigned download paths remain usable. Stop all older application/library writers and back up the full profile and media manifests before upgrading. New profile/media leases do not coordinate with older writers; concurrent old/new access and downgrades are unsupported. Fresh profiles use the MeowChan name, while an existing installation's selected profile/destinations remain in place. `MEOWCHAN_PROFILE` selects an absolute profile path. New profiles default to **Barebones, Dark and English**, with the view’s default density. Main-window theme/view changes save immediately; pending Settings changes require a successful **Save**. Cancel reloads confirmed settings; neither action leaves the page.
- **Double-click a thread row/card to open its gallery**; right-click offers reading, gallery and HTML export in Archive. Board double-click retains details. Open folder and thread details remain explicit actions. Escape closes details; Ctrl+L/Cmd+L switches to Downloads and focuses URL input, and Shift+Enter inserts a newline.
- Progress is **saved / selected media**, with rule exclusions shown separately; it is not bytes or a final thread size. Monitoring continues at 100%. “Archived” describes the source, not a local HTML archive. The import report describes the latest import, not a persistent history.
- View/theme/language preferences and window geometry persist. Density, reading size, column visibility/width/order, archive layout and reader split are saved separately for Barebones, Simple and Advanced.
- .NET 10 and the existing platform targets are retained. Library consumers must use the `libmeowchan` package/assembly and `MeowChan.*` namespaces. No binary redirect is provided. Durable retry/lifecycle state requires explicitly passing `WorkStateStore`; the default is in memory.

### Release status

0.9.0 is in preparation. Final native checks of the current candidate and a Windows user test remain open. Windows ships as a self-contained ZIP with VLC; macOS Intel/Apple Silicon use self-contained, ad-hoc-signed DMGs with VLC, without notarization. Linux x64/Arm64 each have bundled-VLC and system-VLC tar archives plus a bundled AppImage. See the [package-to-offline guide](docs/Help.md), [0.9.0 release-notes draft](docs/releases/v0.9.0.md) and [platform validation record](docs/platform-support.md).

### Additional work on the current branch

The branch now also contains live Diagnostics, a persistent Archive with an offline post/media reader, and basic inherited media rules. Publishing and final release acceptance remain separate decisions.

- Media admission is bounded (256 pending reservations by default), cancellable and fair between manual and board-discovered work; revision successors share that pending budget. Board origins survive restart, and explicit URL admission promotes a tracked thread to manual priority.
- Sources shows registered adapter capabilities and actual watch observations, with shared URL admission, canonical source rules and exact-watch navigation. Registration does not indicate site availability; external providers may optionally declare capabilities without an interface-breaking change.
- Diagnostics shows current watch failures, request/cooldown state, content-storage failures and session warnings, with copy and exact-watch navigation.
- Archived posts embed verified local image thumbnails. Visible images load asynchronously with bounded concurrency; clicking opens the shared gallery. Compact posts stack images above wrapped text, while wider readers put images beside text. Unsupported, missing or mismatched attachments remain explicit.
- The local SQLite catalog retains observed posts for 4chan/8kun. Media may remain on NAS drives; slow media discovery does not block local reading. Removing monitoring preserves captured content. New watches capture posts; restored older watches start media-only until explicitly enabled.
- Rules resolve allowed extensions and maximum metadata file size through Global → Source → Board → Thread, with visible origins and durable first decisions. Existing decisions and files survive later changes. Unknown sizes remain allowed; this is not a transfer-byte cap.
- Manifests retain saved media versions, and the independent library reader verifies selected bytes. Changed size/hash during an active download schedules the latest revision afterward.

Progress now uses **saved / selected media**, with exclusions shown separately. The additive `ThreadContentRuntime`, `ArchiveReader`, rules and media-reader APIs remain independent of Avalonia. Back up the profile and media manifests before upgrading: v1 manifests are accepted, but the new v2 history is not a downgrade format for older binaries. The writer also migrates schema-1 archive catalogs atomically to schema 2 for indexed search and quotes; old schema-1-only readers cannot read the upgraded catalog. The current passive reader can still read schema 1 without migrating it.

Sources, persistent pause, local search/quote navigation and single-thread HTML export are implemented. Collection exports, structured packages/import, TUI/daemon clients and dynamic plugin loading remain later work.

## 0.8.0 (released as oChan)

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
- **Library integration changes are breaking.** Source integrations must adopt explicit dependencies, asynchronous session operations and snapshot notifications. Desktop configuration is no longer part of the library. See the [session API](docs/Registry.md) and [package consumer sample](samples/LibMeowChan.Consumer/README.md).

### Packages and validation

Packaging targets Windows x64, Linux x64/Arm64 and macOS Intel/Apple Silicon. Release builds use a pinned SDK and reviewed dependency locks, with expanded offline tests, graphical checks and package validation. Extract the complete archive, including its native libraries and notices.

Final native acceptance of the extracted packages is still outstanding across the target matrix. macOS bundles are unsigned and not notarized; native launch and Gatekeeper validation remain open. Linux requires glibc and an X11 or XWayland desktop. The [platform validation record](docs/platform-support.md) lists prerequisites and the evidence available for each target.
