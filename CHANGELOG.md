# Changelog

## 1.1.0 — unreleased — Media page, own tags, Danbooru and plugin SDK 1.1.0

- Draft, not published: no release date, package or verification. See the
  [draft release notes](docs/releases/v1.1.0.md), which mark what is still pending.
- Add a Media page over every saved file with filters, duplicate grouping by SHA-256,
  multi-select tagging, gallery and original export, and a way back from the thread.
  Tiles show favorite, rating, own tags and the video poster, and tags can be edited on
  the tile.
- Share one right-click menu across the Media page, Archive table, tiles, inline
  attachments, gallery and video overlay (same order, labels and reasons; also via
  Shift+F10 and the Menu key); the gallery menu now offers own tags.
- Add private own tags for threads and exact media versions, with Reader and gallery
  editing, Archive tag filters, a Tag manager, cleanup protection and tag hand-over
  when a source changes an attachment key. Loose and legacy files that no captured post
  binds can be tagged too, with orphan check and retryable cleanup; own tags are not
  exported.
- Rework the media tag dialogs around search: one field filters a single list of toggle rows
  with use counts, the tags on the file first, then the most used, then A to Z (or sort by
  most used), with one-step undo from the footer. Tagging several files together shows for each
  tag whether all, some or none of the ticked files carry it ("3 of 5"). A vocabulary of 20000
  tags opens and filters smoothly, and the tag manager gets the same sort choice.
- Give tags, filter values and badges one chip look: own tags are filled, thread tags carry a
  house mark, Danbooru source tags have a dashed outline and a category mark, so they differ by
  shape and not only by colour. Every active Media page filter shows as a removable chip under the
  toolbar, even while the filter area is closed, with **Reset all** and a count on the button
  ("Filters (3)"). Tiles show their state in fixed corners (type, provider, not saved,
  no media tags yet, favorite star on hover or focus).
- Select several files in a thread's Media tab, as on the Media page: Ctrl/Cmd+click,
  Shift+click, Shift+arrows, Ctrl/Cmd+A, Esc. With two or more selected, **Tag together**,
  **Selection as gallery**, **Export originals** and **Clear selection** act on all of them, also
  from the right-click menu.
- Add **Import files…** (menu, or drop files and folders on the window) for your own files.
  MeowChan checks each file by SHA-256, copies new ones to `meowchan-import/` in the download
  folder and only links files it already has; your originals are never moved or changed. You can
  add own tags for the new files, stop after the current file, and open the batch on the Media
  page; imported files are loose, taggable media in the index and gallery. The dialog names the
  settings row when a limit stops a selection.
- Show reader posts as cards on the page tone, with the opening post directly on the page. The
  header is one line (subject, name, date, number); a click on the number copies `>>number` for
  quoting. Thumbnails sit flat under a file line with name and size, lines starting with `>` are
  green, and `>>number` in the text jumps to that post (the quote buttons stay as the keyboard
  path). Post bodies are set in a bundled Noto Sans subset (Latin, Greek, Cyrillic; CJK uses the
  system fonts), credited in About and the README next to Inter; the interface stays in Inter. The
  HTML export follows the same cards, colours and greentext.
- Reader: rest the pointer on a quote, or focus it and press Space, to preview the quoted post
  (Esc closes it, Enter jumps there) and see the target flash after a jump. A line marks where new
  posts begin ("N new posts since last time") and **Back to last read** returns to it. Posts over
  24 lines fold to 18 with **Show more** (remembered per post; find opens them). The reply counter
  in the header opens the replies as nested cards, five more at a time. References to posts that
  were not captured are struck through, spoilers stay hidden until you click them or press
  Enter or Space, code is monospace, external links are underlined; the HTML export shows the same.
- Add the setting **Animations** (Follow system, On, Off): short fades, hover and chip transitions
  and toast changes follow the system reduced-motion setting or stay off. Progress bars and
  spinners always stay.
- Draw the keyboard focus ring in two shells so it stays visible on any background, and show the
  selected state by shape on chips, tiles, thread media tiles (drawn check mark) and the settings
  sidebar (a bar), not only by colour.
- Give every window and dialog the app's own title bar. Windows and Linux keep Snap, resize and
  shadow; Windows 11 gets square corners; macOS keeps its native traffic lights, full screen and
  rounded corners with the app caption beneath. `MEOWCHAN_WINDOW_CHROME=system` brings back the
  platform title bar everywhere.
- Announce a found update with a quiet toast (**Release notes**, **Later**, **Skip this version**)
  and a tray entry; the footer notice stays as the indicator. Each version is announced once per
  start, never for a skipped version, with automatic checks off, or when information notifications
  are off. Nothing is downloaded automatically.
- Downloads: removing from the watch list offers **Undo**, retries state the reason and the next
  step in plain language, the tray menu can pause and resume, the search also matches the status
  text, a link to **Show in list** appears for addresses that are already watched, and toasts last
  by category.
- Gallery and Media page: a saved file without a viewer shows **Open with default program** and
  **Show in folder** (runnable types such as scripts are never handed to the default program),
  **Copy file path** joins the shared menu, the gallery export button reads **Original…**, the
  slideshow duration is a dropdown next to the playback mode, the reader post menu copies text,
  number and (4chan) link, and the archive media footer has **Manage saved ratings**.
- Make German and the counted texts consistent: always "du", one wording per action
  (Reader, Downloads, Beobachtung, Post), correct singular forms (also Russian 2 to 4), and
  one file-size format that never shows 0 MB for a small file.
- Add the Danbooru source, read-only and enabled by default (it sends nothing until you
  search or a watcher checks): tag search with restricted previews, single posts, watchers
  that continue below a post id cursor without a page limit, a one-time check of a paused
  watcher, readable errors in all five languages, provenance and offline use. It is
  reachable from the Workspace menu and bundled with the plugin package. Downloaded
  Danbooru originals show in the Media page, gallery and favorites; own tags on them are
  not available.
- Danbooru window: a search shows at most 5 pages of 20 results; every result and watcher
  card offers **Open post in browser** and **Copy link**; a **Danbooru watcher**
  notification reports originals that a check added to the download queue; the detail
  dialog copies source tags and uses up to two of them as a search; pending originals
  are picked up in the background at start. Result cards list rating, score, favorites,
  date and original file as facts with the same rating and tag-category names as the detail
  dialog, and every source tag is a button that adds it to the search (up to two; nothing is
  requested until you press Search). Several Danbooru search addresses in one Add input open the
  window with the last one and report the unused ones once; words after a space stay part of
  an address; a dropped board name such as `/g/` counts as a link, not as a file. Any text
  that mentions a Danbooru host is redacted from logs and feedback context.
- Add the settings **Danbooru catalog limit (MiB)** (default 32, 8 to 128), **Own tag
  limit** (default 1024, 1024 to 20000) and **Own tag file limit (MiB)** (default 32, 8 to
  128). The Danbooru limit warns in the watcher list from 75 %; a refused new tag or write
  names the setting in the user's language. Lowering a limit deletes nothing and never makes
  a saved file unreadable; removing, renaming and cancelling keep working above a lowered
  limit. They sit on the renamed settings page **Storage and limits** (formerly named after
  the plugin-process expander) next to the free-space reserve. The first-run text and the
  Danbooru warning now point to that page, Downloads shows the effective parallel limit live
  with a short hint, and plugin settings say that they apply at the next start.
- Make the remaining amount and size limits settings under **Storage and limits**:
  entries per favorite/thread-favorite/rating list (default 50000) and their file size
  (8 MiB), post bookmarks (10000), saved searches (500), reader groups (500), the question
  before "Export all matches" (500 files or 2 GiB), the video preview cache (128 MiB,
  256 files), the seven import limits (including 32 tags per batch and 100000 batches) and
  the media index cache (256 MiB, 1000000 entries, 200000 threads). Lowering a limit deletes
  nothing; a full list names the setting in the user's language, and the status bar warns
  from 75 % of the favorites, thread favorites and ratings limits and of the media index
  cache. Fixed technical limits are listed in the help.
- Raise the minimum height of the archive metadata, gallery details, media occurrences,
  reader-group prompt, saved searches, experimental activation and startup error windows so
  the footer stays visible below the title bar.
- Add plugin SDK 1.1.0 (booru post search, blocked-declaration status), reviewed and
  moved to Shipped, with a one-time automatic transition for bundled plugins that only
  change the declared SDK version. The booru post identity is `BooruPostId` (renamed before
  the freeze; JSON shape unchanged).
- Bundle five further experimental chan sources (disabled by default) and two documented
  technical blocks; stop passing the 2ch provider MD5 as an integrity expectation, so archived
  2ch originals are fetched once more on re-check.
- Add **Inspect transfer package…** in the Archive header: packages are verified and staged
  before any write, planned as a new thread or a merge, applied only on confirmation;
  several threads apply one after another with a receipt; **Check again** re-reads a
  package changed on disk.
- Record byte-level merge conflicts and review them under **Manage conflicts** (keep,
  discard, or use as the active version, each after confirmation); reclaim unreferenced
  discarded alternatives after a confirmation.
- Offer to discard an import or merge that stopped before its commit, in a two-step
  question at startup.
- Accept picked or typed Archive source/board/state filters immediately; show archive status
  as an overlay; open filters over the results; keep the Reader toolbar on one row.
- Show the orange cat in the light theme and the grey cat in the dark theme; soften the light theme
  (palette corrections pending).
- Show the theme-appropriate cat in the header and both cats side by side in the About window for documentation
  and credits (window, tray and dialog icons keep following the theme), and credit the photographer of both cats (Simba, orange; Mila, grey),
  Alexander Huxel · Webentwicklung Huxel, in THIRD-PARTY-NOTICES, the README and About > Credits.
- Keep Archive, Downloads and Settings usable while the session is still starting; writes
  wait for Ready.
- Write a rolling per-user log file outside the profile and show unexpected UI-thread
  errors as a banner instead of crashing.
- Restore every readable watch past an unreadable work journal row, restart failed
  download loops on the next signal, and open Ctrl/Cmd+F find in the large Reader.
- Return focus to the export button or **More** after closing export, scroll the settings
  validation error into view in small windows, and keep secondary windows at normal size
  after a title-bar restore on Linux/X11.
- Rework the default keyboard shortcuts. **Media** is **Ctrl/Cmd+3** in footer order, with
  **Rules 4**, **Sources 5**, **Diagnostics 6** and **Plugins 7**; footer, menus and the F1 help
  list the pages in the same order. New defaults: **Ctrl+T** tags, **Ctrl+D** thread favorite,
  **Ctrl+Shift+F** filters (also for board filters, which moved from Ctrl+Shift+R), **F3** and
  **Ctrl+G** for find next and **Shift+F3** and **Ctrl+Shift+G** for find previous in the large Reader,
  **S** slideshow, **I** details, **R** rating, **Ctrl+E** export original and **Ctrl+O** show in folder
  in the gallery and Media page, **Ctrl+A** select all media, the Menu key for the context menu, one
  **Alt+Left** Back and one **Alt+PageUp/PageDown** page gesture for the commands that never apply
  together. macOS gets its own defaults (Cmd for Ctrl, no Fn-only keys, Cmd+Delete, Cmd+Left, Option+Cmd+R
  to refresh, Control+Cmd+F for full screen), Apple glyph notation, and a native menu bar. **Settings,
  Keyboard shortcuts** can turn the single-character shortcuts off. Saved shortcuts are never changed:
  a new default yields to a saved binding of another command instead of silencing it, and the startup
  warning appears only for bindings that are really blocked.
- Speed up large collections: the saved media index loads in the background with a status
  instead of freezing the window, is written only when it changed, and unchanged threads and
  imported batches are reused without opening their manifests (100000 entries: warm build 4.8 to
  2.1 s, about 80 % fewer allocations on load); the own tag store and the import catalog are
  indexed and skip re-parsing an unchanged backup; the tag list lays out again only when the filter
  text really changed; finding the reading line no longer walks the visual tree on every scroll;
  the download speed timer stops while the window is hidden or minimized; opening a folder on an
  unresponsive network share no longer freezes the window (2 s limit); chan plugins now ask for
  compressed responses.
- Fix write and queue problems found in review: pausing, resuming, removing and cancelling in the
  Danbooru catalog keep working above a lowered limit (only growth is refused), a failing
  acknowledgement can no longer fail an original already accepted into the queue, and leftover
  temporary catalog files from a crash are cleaned up at the next start.
- Improve screen reader and keyboard use: hidden spoilers get a neutral name until revealed,
  tiles and Danbooru facts have named values (type, untagged state, "term: value"), the quote
  buttons stay reachable with Tab after the post text.
- Show every file size in binary units (KiB, MiB, GiB, TiB; 1 MiB = 1024 KiB), matching the
  limit and bandwidth settings: tiles, export, import, the gallery file plate, attachments and
  the Danbooru window now use one format ("1,5 MiB" in German, "1.5 MiB" in English). The size
  limit of a Rules editor is entered in MiB; saved rules keep their exact byte count, so an old
  "10 MB" rule now shows as 9.536743 MiB. A language pack may be 2 MiB.
- Upgrade: archive schema 5 to 12, media manifest 4 to 5 (6 for imported representations),
  work journal pruning with one backup and a `user_version` stamp, new `private-tags.json`,
  media index cache (version 4; version 3 files are still read, so there is no cold rebuild
  after the update), `booru-catalog.json`, `local-imports.json` with the `meowchan-import/`
  folder, and the new limit and animation settings.
  `private-tags.json` becomes version 2 at the first tag on a loose file; the exact version
  1 file is kept permanently as `private-tags.v1.bak`. Back up the whole profile first;
  there is no supported downgrade.

## 1.0.2 — 2026-10-31 — stability, media controls and startup recovery

- Automatically activate compatible in-place rebuilds of already established bundled
  plugins at startup, including local `dotnet run` builds and an explicitly selected
  bundle rebuilt before its first activation. Preserve exact settings,
  enabled/disabled choices and rollback history, without another confirmation/restart
  loop. Current delivered and cached bytes are reverified; pins, changed declarations,
  special rights, ambiguous targets and unproven legacy history remain guarded.
- Automatically continue restored watches after proven initial source admission waits,
  retaining real failure diagnostics and check intervals after attempted requests.
  Correlate metadata history through host-owned worker contexts and retire it safely.
- Offer explicitly reviewed NEW selections of currently delivered bundled plugins
  for unproven missing legacy candidates, with exact settings and immutable actual-
  state backup. Preserve enabled choices; never silently transfer pins, permissions
  or source ownership. Conflicting and incompatible entries remain blocked.
- Preserve the first validated v1 plugin-state envelope and its digest before startup
  mutations; do not fabricate historical backups for profiles already written as v2.

- Schedule multiple user-prioritized thread watches without preempting active files
  or starving runnable Normal work. Keep the original manual/board origin policy
  for Normal downloads and the existing bandwidth/default settings.
- Bound effective transfer concurrency at 32 while retaining larger saved settings;
  distinguish configured and effective values in the Downloads summary. Healthy
  initial media admission no longer consumes the shared network-header deadline.
- Keep callback-backed silent MP4 playback progressing through the verified handle;
  include the required avformat demuxer in Linux bundles. Hide only covered Reader
  pagination adorners so they cannot intercept gallery-close input.

- Animate GIFs through bounded verified frame decoding, preserving encoded loops,
  transparency/disposal, pause/resume, zoom and safe retirement on media changes.
  Reader posters open the actual associated gallery animation; no audio is invented.

- Keep the selected repeat mode and media-focus/layout choices across gallery
  openings and application restarts. Existing profiles retain the docked layout.
- Add Manual, Repeat and mixed image/video Slideshow modes. Slideshow is started
  explicitly; images use a configurable display period and videos advance after
  their actual end. Pausing, reading context or losing interaction stops progress.
- Reveal transport at the bottom edge and post context at the right edge, without
  reopening controls for movements in the middle of the image. Pointer focus on
  slideshow Start no longer pins controls; keyboard focus, gestures and menus stay protected.
  Hover changes no saved preference. Keep opaque text and a native docked fallback.
- Pair controls overlay and auto-hide in one option. Replace permanent boxed edge
  indicators with subtle borderless chevrons shown briefly on mouse motion.
  Hidden controls no longer consume the first navigation shortcut.
- Honor autoplay for the first/manual video even with saved Slideshow mode,
  without starting automatic next. Preserve native play/pause intent across
  seeking, volume changes and panel interaction.
- Resume timeline tracking after seek even with pointer hover or keyboard focus;
  route effective media navigation after timeline/volume input, use gallery Up/Down
  for available video volume, and disable timeline keyboard seeks without changing pointer seek.
- Keep the narrow thread reader full-height: its navigation cross overlays the
  posts instead of reserving a bottom strip. Scrollable last-post clearance keeps
  the final content readable; the scrollbar and pagination remain unobstructed.
- Preserve newer gallery volume changes when saving an unrelated Settings draft.
- Restore known thread titles from local metadata, including media-only watches;
  use a cleaned opening-post excerpt only when no real title exists.
- Make central banners and notification toasts explicitly dismissible. Dismissal
  suppresses matching repeats for the session without removing diagnostic history;
  passive toast expiry is not acknowledgement.
- Record clearer plugin-startup failure phases while retaining explicit activation
  choices. The historical Windows/Leftypol incident has not been reproduced.

- Stabilize Windows native-overlay clipping to break redundant clip/focus-adorner
  layout feedback, without claiming a general framework subscription-retention fix.
  Retire overlay controls terminally without cross-root return; reject late pointer
  and timer callbacks after stop/detach and avoid detached coordinate conversion.
  Hide the separate video overlay on image/Reader transitions so an empty owned
  window does not block image slideshow progress as a false dialog.
- Preserve observed macOS fullscreen-entry frames across exit so delayed native
  callbacks do not overwrite the restored window size; unknown user resizes still win.

See the [1.0.2 release notes](docs/releases/v1.0.2.md) for the exact-source
publication gate and evidence limits. Native output checks do not establish
physical-speaker output; the manual Windows field test follows publication.

## 1.0.1 — 2026-10-26

- Keep UI queries independent of slow download commits through background state
  projection, with bounded interactive work and coalesced preference updates.
- Reduce repeated view work through coalesced progress, incremental download rows
  and deferred refreshes for covered archive and reader views.
- Replace repeated filter staging scans with conservative temporary-storage
  accounting, preserving recovery and cancellation handling.
- Reduce gallery context and thumbnail work while retaining full original-media
  verification, including before cached video posters are used.
- Coordinate video switching and shutdown with actual native cleanup completion;
  stale operations cannot control newer playback.
- Preserve the visible reader post after delayed reply counts, guard archive
  favorites during shutdown, and honor later macOS Normal-window requests during
  fullscreen restoration.

See the [release notes and downloads](docs/releases/v1.0.1.md). Early user feedback
reports better responsiveness; the original Windows/external-USB hang has not
been formally demonstrated fixed. No universal speedup is promised.

## 1.0.0

MeowChan keeps its compact Barebones view and existing download, archive and
reader workflows while moving the desktop to Semi + Ursa and adding editable
source and image-filter plugins.

### What's new

- **Plugin sources.** The desktop runs 4chan and 8kun through isolated workers;
  the standalone library still offers explicit provider registration. Lainchan,
  Kohlchan, Wizchan, Endchan, 2ch, Leftypol, Uboachan, ZZZchan and SushiChan
  extend the delivery to eleven sources. Five local filters cover dimensions,
  file rules, SHA-256 blocklists, animation/format and PNG metadata presence.
  Fourteen editable examples are included.
  The bundled compiler builds local C# files without an installed .NET SDK or
  runtime package downloads.
- **Plugin management.** Inspect candidates, edit files, validate settings and
  run an isolated test without creating watches or archive entries. Activation,
  pending changes, pinned releases and explicit last-good selection are stored
  per profile. Image access requires the applicable rule and source grants.
- **Bounded work.** Separate metadata admission, worker limits and target-space
  checks keep unrelated downloads moving. A blocked target retains its work and
  can be checked explicitly. Image filters inspect the same original bytes that
  are subsequently committed; exclusions and technical failures remain distinct.
- **Desktop and reader refinements.** Local download search, explicit action
  counts, clearer errors, keyboard navigation, accessible field labels and
  optional last-reading-position resume accompany the theme migration. Reader
  navigation sits at the lower right without covering posts, and video captions
  stay clear of the post context. Barebones remains a complete persistent view.
- **Archive counts and video controls.** Saved media counts no longer depend on
  the current download generation. Video uses one Play/Pause control, per-media
  repeat and audio controls that explain missing tracks or output.
- **Update checks.** Settings and About expose version checking, a small update
  notice and platform download links. Checks do not install updates.

### Source integrity additions

- Provider SHA-1 is verified against the original bytes, including before image
  filters receive access. Size/MD5 exceptions never bypass SHA-1 or SHA-256.
- Rounded provider sizes use a separate raw hint and do not become exact byte
  expectations or rule limits. Saved file sizes still describe measured bytes.
- Archive schema 5 and media manifest 4 preserve these fields. Current readers
  retain support for earlier schemas/manifests; older applications cannot write
  these new formats. Existing revision keys without the new metadata stay stable.
  Library/SDK positional constructors are unchanged; providers opt in through
  `ExpectedSha1` and `ProviderSizeHint` initializers. SHA-1 uses Base64 of 20 bytes;
  local asset identity and integrity continue to use SHA-256.

### Breaking (Library/SDK)

`libmeowchan` and `MeowChan.PluginSdk` are not stable APIs in 1.x; minor releases
may contain breaking changes. Recompile library consumers against this version.
`CapturedPost.Ordinal` and its constructor parameter now use `long` to retain
the full order value supplied by a source. Consumers with explicitly typed
ordinal variables should use `long`; existing persisted integer values remain
readable. Plugin SDK references remain independent of the desktop and library;
the optional Host package supplies compiler and worker integration.

The draft-only `ArchiveSearchSpec.WorkFilterFromIndex` / `WorkFilterToIndex` and
`MediaRatingProjection` UI helpers are now internal. Consumers should use
`ArchiveWorkFilter` and rating values 1–5 / null, mapping their own UI filter
indices and sort order. Stored search and rating formats are unchanged.

New `ArchiveCatalog.CaptureAsync` observations now receive exact attachment
revision keys, even when a caller omits the key. A custom provider that downloads
without acquisition/attachment references and later captures only a matching
media ID/size/MD5 no longer gets an implicit gallery/export association. A local
historical unchanged consumer binary passed with its original Core DLL and
failed its gallery association check after the DLL was replaced; preserved
call signatures do not preserve that earlier behavior. Recompiling alone does
not migrate the old logic: freeze the complete provider metadata, compute
`MediaRevisionKeys` acquisition and post/position references, supply them in
`DownloadItem` before enqueueing, and capture the same observation afterward.
The [updated package consumer](samples/LibMeowChan.Consumer/README.md) demonstrates
this path. Genuinely old stored null-key attachments retain strict legacy
matching; new observations do not receive an unsafe legacy fallback.

### Upgrade and validation

Existing source identities, original media paths, archives and watch state are
retained. Archive and work-journal migrations preserve source ordering and keep
media variants separate. Extract the complete desktop payload, including its
`plugin-worker`, `plugin-compiler` and `plugins` directories. Authoring and
hosting instructions are in the [plugin guide](docs/plugins/README.md).

Package source, automated validation run and checksums are recorded in the release
inventory. Physical hardware, accessibility and everyday-use coverage retain
the limitations in the [release notes](docs/releases/v1.0.0.md).

## 0.9.0

The first stable release under the **MeowChan** name introduces a redesigned Avalonia desktop interface, with Barebones as the simple default for new profiles and Simple and Advanced views for more detail. The 4chan/8kun download engine and durable sessions remain; the independently usable library is renamed to `libmeowchan`.

### What's new

- **Shared local gallery.** Thread context actions, saved-media tables/tiles and inline images open one viewer with Left/Right, Fit/1:1, zoom/pan and saved-post context. Gallery navigation from search includes every found thread in current sort order across result pages, including images from nonmatching posts; saved-media navigation follows its sort/version filter. Animated images show a first frame. Preview safety limits are 64 MiB and 80 million source pixels.
- **Full-window reader and video.** Double-click a thread row/card to read captured posts; Shift+Enter or the context menu opens its gallery. Reader/gallery navigation retains the originating selection and search. Closing the gallery returns to the post associated with the currently shown medium. Saved videos have offline poster previews and play through LibVLC in the gallery. F11 toggles fullscreen across the workspace, reader and gallery.
- **Durable media follow-up.** Consecutive 404s pause 15/30/60/120/240 minutes, then at least 240. A terminal source gets a fixed 12-hour follow-up from its first durably recorded end. Restart/pause do not extend it; completion can retain missing 404 media. Other exhausted/capture/storage failures require intervention. Check-now respects media pauses; manual retry grants one outer attempt, including after completion, without a new watch or end window.
- **Compact repeat export.** Known export targets survive restart. Choose a new folder or explicitly update an inspected earlier export; Open folder opens the current successful result. Modified or reduced output preserves the previous folder after confirmation.
- **Selection and archive cleanup.** Multi-select watches and archived threads for copy, removal and portable combined HTML export. Permanent archive deletion previews the files and requires confirmation; ordinary Delete removes watches while keeping their archive. Favorites track exact saved-media revisions and verified originals can be exported without a new download.
- **Board admission.** A preview wizard filters discovered threads by literal title/opening-post text and shows reasons and individual choices before adding a batch. Removed board threads remain excluded until reactivated; filter changes affect future admissions.

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
- **Double-click a thread row/card to open its reader**; Shift+Enter or the context menu opens its gallery. Archive context actions also include HTML export. Board double-click retains details. Open folder and thread details remain explicit actions. Escape closes details; Ctrl+L/Cmd+L switches to Downloads and focuses URL input, and Shift+Enter inserts a newline.
- Progress is **saved / selected media**, with rule exclusions shown separately; it is not bytes or a final thread size. Monitoring continues at 100%. “Archived” describes the source, not a local HTML archive. The import report describes the latest import, not a persistent history.
- View/theme/language preferences and normal/maximized window geometry persist. Startup fits the default window to the available work area; leaving fullscreen restores the previous window state. Density, reading size, column visibility/width/order, archive layout and reader split are saved separately for Barebones, Simple and Advanced.
- .NET 10 and the existing platform targets are retained. Library consumers must use the `libmeowchan` package/assembly and `MeowChan.*` namespaces. No binary redirect is provided. Durable retry/lifecycle state requires explicitly passing `WorkStateStore`; the default is in memory.

### Release status

Release downloads are ordered as a self-contained Windows x64 ZIP with VLC; self-contained, ad-hoc-signed macOS Apple Silicon and Intel DMGs with VLC; Linux x64 and ARM64 AppImages with VLC; then Linux bundled-VLC and system-VLC tar archives and their corresponding source archives. The macOS apps have no Developer ID signature or notarization, so first launch may need manual approval in Privacy & Security. The 13 binary and source assets have a `SHA256SUMS` companion. See the [package-to-offline guide](docs/Help.md), [0.9.0 release notes](docs/releases/v0.9.0.md) and [platform validation record](docs/platform-support.md).

### Download engine and archive

The desktop also contains live Diagnostics, a persistent Archive with an offline post/media reader, and inherited media rules.

- Media admission is bounded (256 pending reservations by default), cancellable and fair between manual and board-discovered work; revision successors share that pending budget. Board origins survive restart, and explicit URL admission promotes a tracked thread to manual priority.
- Sources shows registered adapter capabilities and actual watch observations, with shared URL admission, canonical source rules and exact-watch navigation. Registration does not indicate site availability; external providers may optionally declare capabilities without an interface-breaking change.
- Diagnostics shows current watch failures, request/cooldown state, content-storage failures and session warnings, with copy and exact-watch navigation.
- Archived posts embed verified local image thumbnails. Visible images load asynchronously with bounded concurrency; clicking opens the shared gallery. Compact posts stack images above wrapped text, while wider readers put images beside text. Unsupported, missing or mismatched attachments remain explicit.
- The local SQLite catalog retains observed posts for 4chan/8kun. Media may remain on NAS drives; slow media discovery does not block local reading. Removing monitoring preserves captured content. New watches capture posts; restored older watches start media-only until explicitly enabled.
- Rules resolve allowed extensions and maximum metadata file size through Global → Source → Board → Thread, with visible origins and durable first decisions. Existing decisions and files survive later changes. Unknown sizes remain allowed; this is not a transfer-byte cap.
- Manifests retain saved media versions, and the independent library reader verifies selected bytes. Changed size/hash during an active download schedules the latest revision afterward.

Progress now uses **saved / selected media**, with exclusions shown separately. The additive `ThreadContentRuntime`, `ArchiveReader`, rules and media-reader APIs remain independent of Avalonia. Back up the profile and media manifests before upgrading: v1 manifests are accepted, but the new v2 history is not a downgrade format for older binaries. The writer also migrates schema-1 archive catalogs atomically to schema 2 for indexed search and quotes; old schema-1-only readers cannot read the upgraded catalog. The current passive reader can still read schema 1 without migrating it.

Sources, persistent pause, local search/quote navigation and both single-thread and combined HTML export are implemented. Structured packages/import, TUI/daemon clients and dynamic plugin loading remain later work.

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
