namespace MeowChan.DesktopSmoke;

internal enum SmokeScenarioKind
{
    // A command-line scenario ("--name"); "normal" runs without one.
    Mode,
    // A top-level MEOWCHAN_*_ONLY selector: valid only in the plain run, one per run.
    Root,
    // A top-level selector without the _ONLY suffix (NAME or NAME=value); scripts/run-desktop-smoke.py mirrors these.
    Branch,
    // Read only inside a mode or another scenario's run; any one of Requires suffices.
    Nested
}

// One registered DesktopSmoke scenario. Pass builds the final SMOKE PASS claim from the
// environment; a nested selector has none of its own (its parent's Pass reads it).
internal sealed record SmokeScenario(string Id, SmokeScenarioKind Kind, string Selector,
    Func<Func<string, string?>, string>? Pass, string[] Requires)
{
    // Nested selectors sharing a Group are alternative runs of that parent: the first one read
    // wins and the others would silently have no effect, so only one may be set per run.
    internal string? Group { get; init; }

    // Extra NAME=value environment of a standalone registry run; {repo} is the repository root.
    // Nested entries also inherit their first requirement's environment.
    internal string[] NightlyEnvironment { get; init; } = [];

    // Why the plain registry run (scripts/run-desktop-smoke.py on private Xvfb) cannot run this entry.
    internal string? NightlySkip { get; init; }

    // False when NightlySkip concerns only this entry's own run and nested entries that require it still run.
    internal bool NightlySkipCoversNested { get; init; } = true;
}

// The single registry of DesktopSmoke scenarios: the selector inventory (SmokeSelectors), the
// usage text and the final PASS claim come from here, and SmokeScenarios.Runs.cs binds each
// Root/Branch id to its run. Adding a check: one entry here plus its run under the same id;
// the smoke driver refuses to start (exit 2) when the two disagree.
// Pure: no window, no profile; DesktopTests compile this file directly.
internal static partial class SmokeScenarios
{
    internal const string NormalPass = "startup, rendered thread/board bindings, background update, normal close";

    private const string Video = "MEOWCHAN_VIDEO_FIXTURE={repo}/tests/Fixtures/gallery-playback-vp8.webm";
    private const string Mp4 = "MEOWCHAN_MP4_FIXTURE={repo}/tests/Fixtures/release102/playback-h264-silent.mp4";
    // The registry run wraps such entries in scripts/with-private-null-audio.py, as CI does.
    internal const string FunctionalAudio = "MEOWCHAN_REQUIRE_FUNCTIONAL_AUDIO=1";
    private const string ExternalDriver = "needs its independent native driver and exchange directory";
    private const string Provenance = "provenance-bound evidence run: needs an executed source digest and a fresh output directory";

    // The pages of the work-views smoke, in capture order (declared before All, which reads it); each has its own alternative run (work-views-<page>).
    internal static readonly string[] WorkViewPages =
        ["downloads", "archive", "reader", "gallery", "media", "booru", "rules", "sources", "plugins", "diagnostics", "statistics", "settings", "dialogs"];

    // Modes in their historical usage order; roots and branches in their former dispatch order.
    internal static readonly SmokeScenario[] All =
    [
        Mode("normal", NormalPass),
        Mode("locked-profile", "locked profile: readable error, Exit closed the window"),
        Mode("no-tray", "no tray: user close kept taskbar access, Exit closed the window"),
        Mode("tray-failure", "tray creation failure: startup succeeded, user close kept taskbar access, Exit closed the window"),
        Mode("download-session", "real session: URL admission, fixture media, assigned path, native close and restart restoration"),
        Alternative("shell-preferences", "MEOWCHAN_SHELL_PREFERENCES_ONLY", "--download-session"),
        Alternative("shell-projection", "MEOWCHAN_SHELL_PROJECTION_ONLY", "--download-session"),
        Alternative("shutdown-order", "MEOWCHAN_SHUTDOWN_ORDER_ONLY", "--download-session"),
        Mode("tray-lifecycle", "16 rapid native window/tray lifetimes, normal close and profile lease reuse"),
        Mode("visual", "actual release showcase with original offline demo content, no watches or HTTP"),
        Mode("reader-ux", NormalPass) with
        {
            Pass = env => ReaderAlternative(env) is { } id
                ? "reader-ux scoped " + id + ": selected journey completed; unrelated reader/gallery journeys not claimed"
                : NormalPass
        },
        Alternative("gallery-close", "MEOWCHAN_GALLERY_CLOSE_ONLY", "--reader-ux"),
        Alternative("reader-media-input", "MEOWCHAN_READER_MEDIA_INPUT_ONLY", "--reader-ux") with { NightlyEnvironment = [Video] },
        Alternative("reader-shortcut-contexts", "MEOWCHAN_READER_SHORTCUT_CONTEXTS_ONLY", "--reader-ux"),
        Nested("pagination-focus", "MEOWCHAN_PAGINATION_FOCUS_ONLY", "MEOWCHAN_READER_SHORTCUT_CONTEXTS_ONLY"),
        Alternative("reader-groups", "MEOWCHAN_READER_GROUPS_ONLY", "--reader-ux"),
        Alternative("reader-returns", "MEOWCHAN_READER_RETURNS_ONLY", "--reader-ux"),
        Alternative("reader-ideas", "MEOWCHAN_READER_IDEAS_ONLY", "--reader-ux"),
        Alternative("reader-replies", "MEOWCHAN_READER_REPLIES_ONLY", "--reader-ux"),
        Alternative("reader-post-filter", "MEOWCHAN_READER_POST_FILTER_ONLY", "--reader-ux"),
        Alternative("reader-stability", "MEOWCHAN_READER_STABILITY_ONLY", "--reader-ux"),
        Alternative("reader-post-width", "MEOWCHAN_READER_POST_WIDTH_ONLY", "--reader-ux"),
        Alternative("video-lifecycle", "MEOWCHAN_VIDEO_LIFECYCLE_ONLY", "--reader-ux"),
        Alternative("window-minimum", "MEOWCHAN_WINDOW_MINIMUM_ONLY", "--reader-ux"),
        Alternative("reader-owned-navigation", "MEOWCHAN_READER_OWNED_NAVIGATION_ONLY", "--reader-ux"),
        Alternative("reader-gallery-roundtrip", "MEOWCHAN_READER_GALLERY_ROUNDTRIP_ONLY", "--reader-ux"),
        Alternative("reader-list-gallery-return", "MEOWCHAN_READER_LIST_GALLERY_RETURN_ONLY", "--reader-ux"),
        Alternative("reader-navigation", "MEOWCHAN_READER_NAVIGATION_ONLY", "--reader-ux"),
        Alternative("reader-view", "MEOWCHAN_READER_VIEW_ONLY", "--reader-ux"),
        Alternative("reader-quick-settings", "MEOWCHAN_READER_QUICK_SETTINGS_ONLY", "--reader-ux"),
        Alternative("reader-thread-menu", "MEOWCHAN_READER_THREAD_MENU_ONLY", "--reader-ux"),
        Alternative("reader-toolbar", "MEOWCHAN_READER_TOOLBAR_ONLY", "--reader-ux"),
        Alternative("reader-conversation", "MEOWCHAN_READER_CONVERSATION_ONLY", "--reader-ux"),
        Alternative("reader-display", "MEOWCHAN_READER_DISPLAY_ONLY", "--reader-ux"),
        Alternative("reader-media-sidebar", "MEOWCHAN_READER_MEDIA_SIDEBAR_ONLY", "--reader-ux"),
        Alternative("reader-inline-video", "MEOWCHAN_READER_INLINE_VIDEO_ONLY", "--reader-ux") with { NightlyEnvironment = [Video] },
        // The registry run uses the first requirement; under --reader-ux it acts only with READER_GROUPS.
        Nested("reader-groups-restore", "MEOWCHAN_READER_GROUPS_RESTORE_ONLY", "MEOWCHAN_URSA_NATIVE_SUITE=groups", "--reader-ux"),
        Mode("selection", "native watch/archive multi-selection, focus-safe commands, batch export return, watch-only Delete and board admission modal"),
        Mode("tray-host", "private StatusNotifier host: independent tray settings, native registration/activation, hide/restore and host/watcher loss"),
        Mode("about-links", "About support, contribution, issue and translation links reached the selected private browser fixture with new-window requests, intact URLs and clean runtime paths"),
        Mode("board-admission", "board admission: local rule clearing, multi-board validation, retry, cancel, EN/DE, themes and minimum dialog layout"),
        Mode("reading-position", "local reading position, explicit process restart and settings semantics"),
        Mode("accessibility-probe", "native accessibility fixture stages completed; external AT-SPI/Orca results are separate") with { NightlySkip = ExternalDriver },
        Nested("accessibility-numeric", "MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY", "--accessibility-probe"),
        Mode("window-geometry", "isolated native interactive resize; each edge/corner after pointer release and settled correction"),
        Mode("video-contrast", "real decoded video time, ended and corrupt-media error contrast in Light/Dark") with { NightlyEnvironment = [Video] },
        Mode("plugins", NormalPass),
        Nested("plugin-reselection", "MEOWCHAN_PLUGIN_RESELECTION_ONLY", "--plugins"),
        Nested("plugin-change-review", "MEOWCHAN_PLUGIN_CHANGE_REVIEW_ONLY", "--plugins"),
        Nested("plugin-page-captures", "MEOWCHAN_PLUGIN_PAGE_CAPTURES_ONLY", "--plugins"),
        Mode("archive-cleanup", "archive cleanup: private preview, explicit confirmation, real deletion, durable invariants and controlled failures"),
        Mode("shell-load", "shell load: bounded offline gallery/load fixture, separate process watchdog, correlated UI/commit/resource measurements"),
        Root("startup-first-frame", "MEOWCHAN_STARTUP_FIRST_FRAME_ONLY", "startup first frame: isolated timing separations and post-startup managed navigation probes; no latency SLO or physical-presentation claim")
            with { NightlySkip = "measurement run: needs a fresh absolute MEOWCHAN_STARTUP_FIRST_FRAME_REPORT path per process" },
        Root("startup-preview", "MEOWCHAN_STARTUP_PREVIEW_ONLY"),
        Root("interrupted-startup", "MEOWCHAN_INTERRUPTED_STARTUP_ONLY"),
        Root("tag-autocomplete-commit", "MEOWCHAN_TAG_AUTOCOMPLETE_COMMIT_ONLY", "UX-LEDGER-11: real quick-tag editor/popup and store, XTest click versus routed Enter/Tab, alias/source/create rows, middle/quoted tokens and commas; private Xvfb only, no Windows/macOS or IME certification"),
        Root("diagnostic-evidence", "MEOWCHAN_DIAGNOSTIC_EVIDENCE_ONLY", "diagnostic evidence: native controls with synthetic snapshots, passive facts/waiting/language refresh; managed input and copy delegate, no live network or OS clipboard/Windows/macOS acceptance"),
        Root("reader-post-star", "MEOWCHAN_READER_POST_STAR_ONLY", "reader post star: real main window, seeded saved pictures; the post file-line star, an archive tile star and the sidebar header star clicked with XTest (embedded reader, large reader opened the Downloads way, scrolled, next page, sidebar), F on the selected post, an unsaved file's star off with its reason; private Xvfb only, no live provider, no Windows/macOS acceptance"),
        Root("reader-preview-return", "MEOWCHAN_READER_PREVIEW_RETURN_ONLY", "reader preview return: saved local attachments through Media and gallery-origin restoration, a delayed or cancelled manifest read, and Retry in the existing reader row"),
        Root("quick-tag-native-typing", "MEOWCHAN_QUICK_TAG_NATIVE_TYPING_ONLY", "quick tag native typing: real main window, seeded video and pictures; the Rate and tag popover opened by R, Ctrl+Shift+T and the media menu over a playing sidebar video and a picture, the field clicked and typed with XTest (keys typed right after R included); text must arrive, the popover stay, the video keep playing, no shortcut run; private Xvfb only, no Windows/macOS acceptance"),
        Root("filename-search-large-reader", "MEOWCHAN_FILENAME_SEARCH_LARGE_READER_ONLY", "filename large reader: seven synthetic native host journeys, complete-reader/off-page results, selection and lifetime joins; managed input only"),
        Root("local-search-language", "MEOWCHAN_LOCAL_SEARCH_LANGUAGE_ONLY", "local search language: EN/DE/ES/FR/RU retained drafts/results, actual native controls and 360-DIP action glyph layout; no OS typing, current-byte or cross-platform acceptance"),
        Root("media-page", "MEOWCHAN_MEDIA_PAGE_ONLY", "media page: real host, catalog, bound manifests, verified thumbnails and navigation; managed input only"),
        Root("media-panel-selection", "MEOWCHAN_MEDIA_PANEL_SELECTION_ONLY", "media sidebar: follows the selected medium and announces each change after the timer; managed input only"),
        Root("focus-click-contracts", "MEOWCHAN_FOCUS_CLICK_CONTRACTS_ONLY", "UX-LEDGER-06: short Media grid/sidebar single/double/right-click and focus contracts over three saved images; managed pointer input only; protected Reader/Tagging/Gallery context gaps are skipped in DesktopTests, not claimed as passed"),
        Root("gallery-exif-orientation", "MEOWCHAN_GALLERY_EXIF_ORIENTATION_ONLY", "EXIF orientations 6/8: real host, strict synthetic JPEG bindings, source and rendered quadrant order in reduced Fit, physical-pixel 100%, media tiles, filmstrip and reader attachments; managed input/private Xvfb only; no live provider, no Windows/macOS acceptance"),
        Root("media-page-booru-captures", "MEOWCHAN_MEDIA_PAGE_BOORU_CAPTURES_ONLY", "media page Booru mode: verified synthetic JPEG tiles and real host tag counts; 1400/760 DIP, Light/Dark captures with visible left sidebar and usable grid; managed input only; no live provider, no Windows/macOS acceptance"),
        Root("media-slot", "MEOWCHAN_MEDIA_SLOT_ONLY", "media slot: reader attachment, archive tile and media-page tile over generated portrait, landscape and video-poster bitmaps in Light and Dark; the visible frame, play button, star, badges and focus frame sit on the drawn image, not on the reserved slot; synthetic bitmaps, managed input only, no Windows/macOS acceptance"),
        Root("media-menu-surfaces", "MEOWCHAN_MEDIA_MENU_SURFACES_ONLY", "media menu surfaces: one real main window and archive; media page, archive table/tiles/inline attachment and gallery open the shared media menu on the right-clicked occurrence, via Menu key and Shift+F10, with identical order, labels and reasons and refusal after rebuild, thread change and navigation; managed input only, no Windows native-video overlay"),
        Root("motion", "MEOWCHAN_MOTION_ONLY", "motion: policy (environment > setting > OS signal) switching motion-off on an open window; hover, overlay, toast and image-fade styles present only with motion on; frame interval of a page fade and overlay reveal measured under software rendering; no GPU or OS-setting claim"),
        Root("gallery-toolbar", "MEOWCHAN_GALLERY_TOOLBAR_ONLY", "gallery toolbar: real main-window gallery at the GL5 threshold matrix 760/1099/1100/1440 DIP (500/500/920/920 high) in Light and Dark, English and German; primary controls kept, rare commands in the overflow menu, post panel as an overlay below 900 dip keeping a minimum image area; managed input only"),
        Root("gallery-narrow", "MEOWCHAN_GALLERY_NARROW_ONLY", "gallery narrow chrome: real main-window gallery with the controls shown at 1920/1280/960/800/640 DIP in English and German over a picture and a video; the way back collapses to its arrow only where its label does not fit, no control is clipped, truncated without an ellipsis, outside the viewer or overlapping, crops of the corners, rows and info line captured; private source-built evidence only") with { NightlyEnvironment = [Video] },
        Root("theme-colors", "MEOWCHAN_THEME_COLORS_ONLY", "theme colors: real main window, Settings › Appearance › Customize colors; the dialog opens on the shown variant, a preset and a typed hex change draft and preview card, unreadable text disables Apply with the reason in words, Apply saves only the colors and the live accent follows, Cancel on a dirty draft asks; four captures (Light/Dark, EN/DE) with an accent override applied; managed input only, no Windows/macOS acceptance"),
        Root("tagging-page", "MEOWCHAN_TAGGING_PAGE_ONLY", "tagging page: real main window, host and tag-queue.json with synthetic queue entries; app-menu entry, progress, Delete/Alt+Down in the list, context menu, stamp editor, state filter, clear question, gallery identity and page visits, three work views at 1440/1100/760 DIP with Light/Dark EN/DE/RU/JA captures and X11 outside-click coverage; no Windows/macOS acceptance"),
        Root("gallery-media-tags", "MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY", "gallery media tags: real main window gallery, host and store, bound and unbound media; managed input only"),
        Root("reader-live", "MEOWCHAN_READER_LIVE_ONLY", "reader live updates: narrow notice, tab switching, manual refresh, live following and thread switching"),
        Root("tagging-mode", "MEOWCHAN_TAGGING_MODE_ONLY", "tagging mode: real main window gallery over a queue of one bound and two loose synthetic media in the real host and stores; stamp and next, Enter, Space, Backspace undo order, Shift+Space, queue position, German strip and the resume point after closing; managed routed keys only, no OS keyboard or Windows/macOS acceptance"),
        Root("tagging-cards", "MEOWCHAN_TAGGING_CARDS_ONLY", "tagging cards: real main window gallery in the tagging mode over a queue of one bound and two loose synthetic media in the real host and stores; K shows five cards (at least 48 by 120 dip, text, key, names), stamp, back, favorite, skip and done cards through the walk's own log and undo, the kept choice, Light and Dark at 1440 and 760, German; managed routed keys only, no OS keyboard or Windows/macOS acceptance"),
        Root("local-media-tags", "MEOWCHAN_LOCAL_MEDIA_TAGS_ONLY", "local media tags: loose legacy file tagged in the real archive menu, dialog, host and store; same tag after reopening a second shell; managed input only"),
        Root("manual-import", "MEOWCHAN_MANUAL_IMPORT_ONLY", "manual import: real second shell and host over a private profile; prefilled dialog check, copy with tag, linked twin, Show batch on the Media page; managed input only, no OS picker or native drag"),
        Root("profile-switch", "MEOWCHAN_PROFILE_SWITCH_ONLY", "profile switch: real second shell and host over a private profile and a private profile list; New profile refuses the current folder, creates a profile with its own download folder and id, Switch profile lists both, opens one and refuses a profile another window holds; recording launcher, no process started; managed input only, no OS folder picker"),
        Root("folder-source", "MEOWCHAN_FOLDER_SOURCE_ONLY", "folder source: real second shell and host over a private profile; Sources › Folders refuses the download folder, adds and scans a fixture folder without hashing; the Media page shows its files, the gallery hashes one on view and favorites it; Remove asks inline; the folder stays byte- and timestamp-identical; managed input only, no OS folder picker"),
        Root("booru-media", "MEOWCHAN_BOORU_MEDIA_ONLY", "booru media: synthetic Danbooru catalog and downloaded originals in the real media page, details, gallery and favorites; watcher removal and a restart without a provider; Light and Dark screenshots; managed input only, no live provider, no Windows/macOS acceptance"),
        Root("private-tags", "MEOWCHAN_PRIVATE_TAGS_ONLY", "private tags: real host and store, actual reader tag bar, suggestions, list rows, table tag column (modes, sorting, rename, unavailable), restart and damaged index; managed input only, no Windows/macOS acceptance"),
        Root("reader-find", "MEOWCHAN_READER_FIND_ONLY", "reader find: actual offline404 native controls, whole-thread hits and managed routed keys; no OS keyboard or Windows/macOS acceptance"),
        Root("filename-search", "MEOWCHAN_FILENAME_SEARCH_ONLY", "filename search standalone dialog: captured metadata paging, literal Unicode input and explicit selection; no product host navigation or current-byte/platform acceptance"),
        Root("media-hash-search", "MEOWCHAN_MEDIA_HASH_SEARCH_ONLY", "SHA-256 search standalone dialog: one captured inventory, exact reference selection and native controls; no global-library search, rehash or product host navigation acceptance"),
        Root("unified-search", "MEOWCHAN_UNIFIED_SEARCH_ONLY", "unified search: archive list and reader search surfaces over a synthetic catalog in a private host window, Light and Dark captures; managed input only, no OS keyboard or Windows/macOS acceptance"),
        Root("filename-search-host", "MEOWCHAN_FILENAME_SEARCH_HOST_ONLY", "filename search host: actual native page/modal, exact offscreen post navigation and causal lifetime drains; managed input, no OS keyboard, detached-reader/fullscreen or Windows/macOS acceptance"),
        Root("urltext-export", "MEOWCHAN_URLTEXT_EXPORT_ONLY", "URLtext export: real dialog consent and create-only writer, synthetic summaries, injected chooser, ES/FR/RU; no OS picker or client interoperability acceptance"),
        Root("urltext-page-boundary", "MEOWCHAN_URLTEXT_PAGE_BOUNDARY_ONLY", "URLtext page boundary: real native owner/page with synthetic summaries and causal lifecycle/modal guards; no OS picker or client interoperability acceptance"),
        Root("gallery-volume-routed", "MEOWCHAN_GALLERY_VOLUME_ROUTED_ONLY", "gallery volume routed boundary: synthetic owner adapter, actual Avalonia Slider/TextBox events; no playback continuity or OS keyboard acceptance"),
        Root("first-run-reopen", "MEOWCHAN_FIRST_RUN_REOPEN_ONLY", "First-run Settings reopen: actual constructors/path controls, abandoned unsaved draft and fresh disk/runtime cap; two synthetic profiles, managed UI, no native Windows/macOS acceptance"),
        Root("dialog-frame", "MEOWCHAN_DIALOG_FRAME_ONLY", env => "dialog frame: fixed header/error strip/footer around a scrolling body per production dialog, group=" + (env("MEOWCHAN_DIALOG_FRAME_GROUP") ?? "all") + "; private fixtures, managed input, no Windows/macOS or screen-reader claim"),
        // Evidence run, not a check: skipped by the default sweep unless an output directory is requested.
        Root("dialog-gallery", "MEOWCHAN_DIALOG_GALLERY_ONLY", "dialog gallery: every dialog, modal, popup, menu, banner and toast over private fixtures in Light/Dark x EN/DE as PNGs plus index.html; closed with Cancel/Escape, no network, no confirmation; review evidence, not a rendering-fidelity or Windows/macOS claim")
            with { NightlySkip = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_DIALOG_GALLERY_OUTPUT"))
                ? "screenshot evidence run: set MEOWCHAN_DIALOG_GALLERY_OUTPUT to a fresh directory to run it" : null },
        Root("ui-catalog", "MEOWCHAN_UI_CATALOG_ONLY", "ui catalog: pages, page modes, overlays, popovers, flyouts, context menus and work-view tiers of the real main window over the offline showcase archive in Light/Dark x EN/DE (pages also narrow) as PNGs plus one index.html grouped by catalog areas; nothing confirmed or saved, no network; review evidence, not a rendering-fidelity or Windows/macOS claim")
            with { NightlySkip = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_UI_CATALOG_OUTPUT"))
                ? "screenshot evidence run: set MEOWCHAN_UI_CATALOG_OUTPUT to a fresh directory to run it" : null },
        Root("html-export-action-layout", "MEOWCHAN_HTML_EXPORT_ACTION_LAYOUT_ONLY", "HTML export dialog action labels: actual single/collection controls, 640/480 DIP, ES/FR/RU; managed input, no Windows/macOS or language-quality acceptance"),
        Root("shortcut-issue-language", "MEOWCHAN_SHORTCUT_ISSUE_LANGUAGE_ONLY", "shortcut issue refresh: real Settings controls and retained validation in five languages; managed input, synthetic profile, no OS keyboard or language-quality acceptance"),
        Root("export-status-language", "MEOWCHAN_EXPORT_STATUS_LANGUAGE_ONLY", "export status refresh: actual single/collection export, backup, failure and cancellation in five languages; offline synthetic profile, managed input, no native Windows/macOS acceptance"),
        Root("reader-groups-binding", "MEOWCHAN_READER_GROUPS_BINDING_ONLY", "reader-group bindings: exact live labels for 3 row states in 5 languages on actual native UI thread; synthetic fixture only"),
        Root("native-transfer-preview", "MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY", "native transfer inventory-only preview: synthetic package, dialog terminal ownership and late picker/close paths; managed input, no Apply or native Windows/macOS acceptance"),
        Root("html-export-labels", "MEOWCHAN_HTML_EXPORT_LABELS_ONLY", "HTML export full labels: production inspector, results overflow and split/large reader layout; EN/DE/ES/FR/RU, recorded scale; managed input, no OCR or cross-platform claim"),
        Root("gif-playback-core", "MEOWCHAN_GIF_PLAYBACK_CORE_ONLY", "GIF isolated playback-core: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("gif-successors", "MEOWCHAN_GIF_SUCCESSORS_ONLY", "GIF isolated successors: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("gif-verified-failures", "MEOWCHAN_GIF_VERIFIED_FAILURES_ONLY", "GIF isolated verified-failures: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("gif-reader-association", "MEOWCHAN_GIF_READER_ASSOCIATION_ONLY", "GIF isolated reader-association: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("gif-ownership", "MEOWCHAN_GIF_OWNERSHIP_ONLY", "GIF isolated ownership: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("gif-playback", "MEOWCHAN_GIF_PLAYBACK_ONLY", "GIF actual rendered animation: encoded loops/disposal, native pause/next, verified failures/retirement and Reader-associated playback; private source-built evidence only") with { NightlyEnvironment = [Video, Mp4] },
        Root("clipboard-monitor", "MEOWCHAN_CLIPBOARD_MONITOR_ONLY", "clipboard monitor: isolated synthetic clipboard, Save/Discard, passive offer, local button confirmation and lifecycle; managed input"),
        Root("booru-workbench", "MEOWCHAN_BOORU_WORKBENCH_ONLY", "booru workbench: real app-menu command over fake ports; exact query, result cards, bounded paging, one explicit load, offline/429/restricted/error/empty states, post-link one-shot, watcher create/check/pause/resume/remove with local-media notice, sdk-mismatch repair route (native Enter → shared review dialog → consent/confirm → restart notice, blocked reason), EN/DE/ES/FR/RU and light/dark screenshots; no network, queue, scheduler or provider"),
        Root("booru-composition", "MEOWCHAN_BOORU_COMPOSITION_ONLY", "booru composition: real session composes Danbooru only while the plugin is enabled; hosted workspace page over the real search adapter, in-process plugin, bounded preview path, shared download queue and watcher scheduler against an in-memory Danbooru; one exact search, previews, one queued original, watcher baseline and check now, light/dark screenshots; no socket"),
        Root("booru-busy-fallback", "MEOWCHAN_BOORU_BUSY_FALLBACK_ONLY", "booru busy fallback: real hosted page loses availability while a pause action is held; the normal Downloads route completes after the busy owner ends, with failure and closing cases; managed routed input only, no live provider or OS input acceptance"),
        Root("download-safety", "MEOWCHAN_DOWNLOAD_SAFETY_ONLY", "download resource limit: real Settings Save/Discard/rejection and fresh host, managed input; no HTTP/load claim"),
        Root("board-language-refresh", "MEOWCHAN_BOARD_LANGUAGE_REFRESH_ONLY", "board admission command-help refresh: UI-thread live accessibility bindings and all seven mapping hints; private controls, no catalog fetch or apply"),
        Root("rating-language-refresh", "MEOWCHAN_RATING_LANGUAGE_REFRESH_ONLY", "rating dialog/manager language refresh: private fixtures, rows/draft/selection/accessibility and provider-call counts; managed input"),
        Root("html-export-language", "MEOWCHAN_HTML_EXPORT_LANGUAGE_ONLY", "HTML export language: actual single/collection Start actions, frozen press-time locale, persisted metadata and escaped source content; ES/FR/RU, managed input only"),
        Root("secondary-settings-language", "MEOWCHAN_SECONDARY_SETTINGS_LANGUAGE_ONLY", env => "secondary Settings language cell=" + (env("MEOWCHAN_SECONDARY_SETTINGS_CELL") ?? "all") + "; real controls, synthetic profile, managed input, no whole-UI/native-platform/linguistic acceptance"),
        Root("gallery-hidden-favorite", "MEOWCHAN_GALLERY_HIDDEN_FAVORITE_ONLY", "hidden favorite controls: exact-key delayed save/navigation and separate idempotent pick/unpick, private PNG and managed input only"),
        Root("settings-language-refresh", "MEOWCHAN_SETTINGS_LANGUAGE_REFRESH_ONLY", "settings/first-run language refresh: rejected drafts, focus and live pending destination check; managed input, private fixtures"),
        Root("settings-reader-display", "MEOWCHAN_SETTINGS_READER_DISPLAY_ONLY", "settings reader-display: native-window Reader/Gallery editor, validation, save/discard/failure/retry, hidden cap and language draft retention, live bounded preferences; managed input only"),
        Root("first-run-settings", "MEOWCHAN_FIRST_RUN_SETTINGS_ONLY", "first-run Settings: real automatic fresh-profile setup to retained editor, persisted nondefaults, unrelated save, restart, Later/X; managed input"),
        Root("first-run-setup", "MEOWCHAN_FIRST_RUN_SETUP_ONLY", "first-profile setup: EN/DE Light/Dark explicit accept/Later/X/errors/probe retirement; private fixtures, no package claim"),
        Root("preference-startup", "MEOWCHAN_PREFERENCE_STARTUP_ONLY", "startup preferences: private held-restore/Ready/native column input/reopen, current source; platform limits explicit"),
        Root("native-drain-boundary", "MEOWCHAN_NATIVE_DRAIN_BOUNDARY_ONLY", "native drain boundary: a faulted native gallery cleanup keeps the shell open and the profile lease held against a second process; the lease is free after the child ends; no decoder"),
        Root("compact-diagnostics", "MEOWCHAN_COMPACT_DIAGNOSTICS_ONLY", "compact and diagnostics: all-mode projection, frozen scope/revalidation/context/acknowledgement; native XTest/SendInput/CGEvent row-and-blank pointer/focus check on an explicitly disposable desktop plus managed checks"),
        // This scene deliberately loads VP8+Vorbis (including paused cleanup). An
        // isolated display without an owned audio service can strand native Stop in
        // ALSA/PipeWire device-open teardown; use the existing private sink owner.
        Root("gallery-errors-retirement-clip", "MEOWCHAN_GALLERY_ERRORS_RETIREMENT_CLIP_ONLY", "Gallery isolated retirement-clip: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("gallery-errors-layout-roots", "MEOWCHAN_GALLERY_ERRORS_LAYOUT_ROOTS_ONLY", "Gallery isolated layout-roots: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("gallery-errors-notifications", "MEOWCHAN_GALLERY_ERRORS_NOTIFICATIONS_ONLY", "Gallery isolated notifications: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("gallery-errors-failure-reader-return", "MEOWCHAN_GALLERY_ERRORS_FAILURE_READER_RETURN_ONLY", "Gallery isolated failure-reader-return: actual native viewer; private source-built evidence only") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("gallery-error", "MEOWCHAN_GALLERY_ERROR_ONLY", "gallery diagnostics-only notices: central acknowledgement, no transport/focus/autohide side effects; true failure safe") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("feedback", "MEOWCHAN_FEEDBACK_ONLY", "feedback: real rendered banner/toast dismissal, matching duplicates, queue, stale close and passive expiry"),
        Root("gallery-playback-features", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY", env => env("MEOWCHAN_GALLERY_EDGE_ONLY") == "1"
            ? "native edge-hover panels: slideshow pointer-focus hide, bottom transport/right post, no central-motion reveal or preference write"
            : "gallery playback features: saved choices, edge panels, real mixed-media sequence, replay and decode failure; recorded platform only") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Alternative("owned-control-transfers", "MEOWCHAN_OWNED_CONTROL_TRANSFERS_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"),
        Alternative("cross-root-migration", "MEOWCHAN_CROSS_ROOT_MIGRATION_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"),
        Alternative("native-overlay-visibility", "MEOWCHAN_NATIVE_OVERLAY_VISIBILITY_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"),
        Alternative("gallery-edge", "MEOWCHAN_GALLERY_EDGE_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY"),
        Root("gallery-mute", "MEOWCHAN_GALLERY_MUTE_ONLY", "gallery mute persistence: real OS input and decoder with functional private audio; session intent across selection changes, not physical output acceptance") with { NightlyEnvironment = [Video, FunctionalAudio] },
        Root("media-interaction", "MEOWCHAN_MEDIA_INTERACTION_ONLY", "media interaction: XTest pointer/key input on private Linux X11; decoder/slider sync, custom navigation, loop and retired gesture checks; NOT Windows acceptance") with { NightlySkip = "XTest input and its report run through scripts/test-media-interaction-smoke.py" },
        Root("native-ime", "MEOWCHAN_NATIVE_IME_ONLY", "IME product checkpoints completed; real engine and UI acceptance require the independent native driver") with { NightlySkip = ExternalDriver },
        Root("gallery-integrity-gui", "MEOWCHAN_GALLERY_INTEGRITY_GUI_ONLY", "gallery integrity: four EN/DE Light/Dark cells at actual scale, exact binding, summary/context/details and geometry") with { NightlyEnvironment = ["MEOWCHAN_GALLERY_INTEGRITY_EXPECT_SCALE=1"] },
        Root("smoke-alpha2-copy", "MEOWCHAN_SMOKE_ALPHA2_COPY_ONLY", "alpha2 copy: Sources granted/strict/null/inactive and Rules help at actual scale and minimum size") with { NightlyEnvironment = ["MEOWCHAN_ALPHA2_COPY_EXPECT_SCALE=1"] },
        Root("source-filter-wave", "MEOWCHAN_SOURCE_FILTER_WAVE_ONLY"),
        Root("filter-pointer-selection", "MEOWCHAN_FILTER_POINTER_SELECTION_ONLY", "filter pointer selection: native X11 clicks on source/board suggestions and state dropdown; Enter comparison, draft-only changes and free text; private synthetic profile, no provider requests"),
        Root("archive-media-counts", "MEOWCHAN_ARCHIVE_MEDIA_COUNTS_ONLY"),
        Root("source-integrity-gui", "MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY", "source integrity: focused Sources/Plugins consent and Experimental fixture at the requested actual scale") with { NightlyEnvironment = ["MEOWCHAN_SOURCE_INTEGRITY_EXPECT_SCALE=1"] },
        Nested("experimental-activation-gui", "MEOWCHAN_EXPERIMENTAL_ACTIVATION_GUI_ONLY", "MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY"),
        Nested("experimental-activation-status", "MEOWCHAN_EXPERIMENTAL_ACTIVATION_STATUS_ONLY", "MEOWCHAN_EXPERIMENTAL_ACTIVATION_GUI_ONLY"),
        Root("suggestions", "MEOWCHAN_SUGGESTIONS_ONLY") with { NightlySkip = Provenance },
        // Its NativeFeatureCheckpoint needs the external driver's MEOWCHAN_ACCESSIBILITY_EXCHANGE; the restore run never reaches it.
        Branch("ursa-native-suite-groups", "MEOWCHAN_URSA_NATIVE_SUITE=groups") with { NightlySkip = ExternalDriver, NightlySkipCoversNested = false },
        Branch("media-fit-benchmark", "MEOWCHAN_MEDIA_FIT_BENCHMARK") with { NightlySkip = "benchmark measurement, not a pass/fail check" },
        // Tier: MEOWCHAN_MEDIA_SCALE=S|M|L; report directory: MEOWCHAN_MEDIA_SCALE_REPORT (MediaScaleBenchmark.cs).
        Branch("media-scale-benchmark", "MEOWCHAN_MEDIA_SCALE_BENCHMARK") with { NightlySkip = "benchmark measurement on a large synthetic profile, not a pass/fail check" },
        Root("media-ratings", "MEOWCHAN_MEDIA_RATINGS_ONLY", env => env("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1"
            ? "ratings: targeted direct/menu late failures and detached tile refresh; exact keys and store reopen"
            : env("MEOWCHAN_RATINGS_PRESENTATION_ONLY") == "1"
            ? "ratings: 12-cell real presentation/focus/error matrix and unchanged durable values"
            : "ratings: real controls, exact identities, comparison, retry, detached shortcut, gallery freeze, manager focus and store reopen"),
        Alternative("ratings-late-error", "MEOWCHAN_RATINGS_LATE_ERROR_ONLY", "MEOWCHAN_MEDIA_RATINGS_ONLY"),
        Alternative("ratings-presentation", "MEOWCHAN_RATINGS_PRESENTATION_ONLY", "MEOWCHAN_MEDIA_RATINGS_ONLY"),
        Root("section-comparison", "MEOWCHAN_SECTION_COMPARISON_ONLY"),
        Nested("section-presentation", "MEOWCHAN_SECTION_PRESENTATION_ONLY", "MEOWCHAN_SECTION_COMPARISON_ONLY"),
        Branch("template-comparison", "MEOWCHAN_TEMPLATE_COMPARISON") with { NightlySkip = Provenance },
        Nested("template-comparison-presentation", "MEOWCHAN_TEMPLATE_COMPARISON_PRESENTATION_ONLY", "MEOWCHAN_TEMPLATE_COMPARISON"),
        Branch("archive-comparison", "MEOWCHAN_ARCHIVE_COMPARISON") with { NightlySkip = Provenance },
        Alternative("archive-comparison-exact-ids", "MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY", "MEOWCHAN_ARCHIVE_COMPARISON"),
        Alternative("archive-comparison-locale", "MEOWCHAN_ARCHIVE_COMPARISON_LOCALE_ONLY", "MEOWCHAN_ARCHIVE_COMPARISON"),
        Alternative("archive-comparison-matrix", "MEOWCHAN_ARCHIVE_COMPARISON_MATRIX_ONLY", "MEOWCHAN_ARCHIVE_COMPARISON"),
        Alternative("archive-comparison-state", "MEOWCHAN_ARCHIVE_COMPARISON_STATE_ONLY", "MEOWCHAN_ARCHIVE_COMPARISON"),
        Root("saved-searches", "MEOWCHAN_SAVED_SEARCHES_ONLY") with { NightlySkip = "restart journey across processes with an explicit disposable MEOWCHAN_SAVED_SEARCH_TEST_PROFILE" },
        Root("footer-navigation", "MEOWCHAN_FOOTER_NAVIGATION_ONLY"),
        Root("statistics", "MEOWCHAN_STATISTICS_ONLY", "statistics: header graph in every work view and the Header graph setting, the statistics page with synthetic counters, reset question, sampling claim and light/dark/German captures; no request leaves the process"),
        Root("context-menus-header", "MEOWCHAN_CONTEXT_MENUS_HEADER_ONLY", "context menus header: the header display menu opens from the Menu key and a context request, six arrangements with the stored one checked, a choice applies at once and persists and shows in Settings, the statistics and settings entries, Escape closes, focus returns"),
        Root("context-menu-layout", "MEOWCHAN_CONTEXT_MENU_LAYOUT_ONLY", "context menu layout: popup menus (builder menus with long labels and long key names, short labels, the catalog commands with the longest shortcuts, a MenuFlyout and the footer More flyout) in Light/Dark and EN/DE measure between 224 and 416 dip, no shortcut is clipped, shortcuts share one right edge with at least 24 dip of room, labels that do not fit are trimmed with an ellipsis and a tooltip"),
        Root("picker-lifetime", "MEOWCHAN_PICKER_LIFETIME_ONLY"),
        Root("settings-category", "MEOWCHAN_SETTINGS_CATEGORY_ONLY"),
        Root("settings-search", "MEOWCHAN_SETTINGS_SEARCH_ONLY", "settings search: typo, abbreviation, synonym and English-name hits across groups with group names, arrow keys, Enter into a collapsed section, empty state, Escape clears then leaves, EN/DE light/dark captures"),
        Root("status-deep-links", "MEOWCHAN_STATUS_DEEP_LINKS_ONLY", "status deep links: the Sources page opens Status > Diagnostics with the source filter preset (clearable), the recovery route (failed restore) reaches the Diagnostics segment, Statistics falls back to it and the source link still applies; managed input only, no network"),
        Root("settings-statistics", "MEOWCHAN_SETTINGS_STATISTICS_ONLY", "settings statistics: nine rows with ranges, switch dependencies, refusal outside the ranges, save/reload/discard through the production path and hub options, EN/DE light/dark captures"),
        Root("settings-lifetime", "MEOWCHAN_SETTINGS_LIFETIME_ONLY"),
        Root("rules-settings-captures", "MEOWCHAN_RULES_SETTINGS_CAPTURES_ONLY", "rules and settings pages: evidence-only captures of the Rules overview (empty and filled at 760/1380/1920 dip, Light/Dark), the Rules page and every Settings category in Light/Dark, EN/DE at 1440 and 900 dip; nothing is saved"),
        Root("rules-walkthrough-captures", "MEOWCHAN_RULES_WALKTHROUGH_CAPTURES_ONLY", "rules walkthrough: evidence-only captures of every Rules page state (overview empty/filled/draft, wizard steps 1-3 for each scope kind, invalid scope and values, save, edit, delete) in EN/DE, Light/Dark at 1440 and 900 dip, with logged row geometry; throw-away profile"),
        Root("path-input", "MEOWCHAN_PATH_INPUT_ONLY"),
        Root("numeric-focus", "MEOWCHAN_NUMERIC_FOCUS_ONLY"),
        Root("media-performance", "MEOWCHAN_MEDIA_PERFORMANCE_ONLY"),
        Root("media-integration", "MEOWCHAN_MEDIA_INTEGRATION_ONLY"),
        Root("text-selection", "MEOWCHAN_TEXT_SELECTION_ONLY"),
        Root("gallery-favorites", "MEOWCHAN_GALLERY_FAVORITES_ONLY", "gallery favorites: hidden-controls saved/pending badge, explicit mark/unmark, exact-key navigation, failure/retry and actual shell failure owner"),
        Root("gallery-theme", "MEOWCHAN_GALLERY_THEME_ONLY"),
        Root("gallery-fit", "MEOWCHAN_GALLERY_FIT_ONLY"),
        // GalleryViewerChecks runs in these branches; FAVORITES returns before FOCUS is read.
        // Under --download-session it replaces DownloadSessionChecks just as the shell alternatives do.
        Nested("gallery-focus", "MEOWCHAN_GALLERY_FOCUS_ONLY", "MEOWCHAN_GALLERY_FIT_ONLY", "MEOWCHAN_MEDIA_PERFORMANCE_ONLY",
            "MEOWCHAN_GALLERY_THEME_ONLY", "--download-session") with { Group = "--download-session" },
        Root("media-tile-panel", "MEOWCHAN_MEDIA_TILE_PANEL_ONLY"),
        Root("diagnostic-feedback", "MEOWCHAN_DIAGNOSTIC_FEEDBACK_ONLY"),
        // Independent normal-tail scenes: one failing page must not hide the next page's evidence.
        Root("normal-core", "MEOWCHAN_NORMAL_CORE_ONLY", "normal head through rendered bindings and synthetic row disposal"),
        Root("normal-board-admission", "MEOWCHAN_NORMAL_BOARD_ADMISSION_ONLY"),
        Root("normal-grid", "MEOWCHAN_NORMAL_GRID_ONLY"),
        Root("normal-settings-page", "MEOWCHAN_NORMAL_SETTINGS_PAGE_ONLY"),
        Root("normal-diagnostics", "MEOWCHAN_NORMAL_DIAGNOSTICS_ONLY"),
        Root("sources-page", "MEOWCHAN_SOURCES_PAGE_ONLY"),
        Root("page-header-help", "MEOWCHAN_PAGE_HEADER_HELP_ONLY", "Sources, Plugins and Booru: visual-tree order, right-edge Help and Tab/Shift+Tab at wide and minimum widths in EN/DE"),
        Root("pause-controls", "MEOWCHAN_PAUSE_CONTROLS_ONLY"),
        Root("rules-page", "MEOWCHAN_RULES_PAGE_ONLY"),
        Root("settings-core", "MEOWCHAN_SETTINGS_CORE_ONLY"),
        Root("page-refresh", "MEOWCHAN_PAGE_REFRESH_ONLY"),
        Root("stat-footer", "MEOWCHAN_STAT_FOOTER_ONLY", "stat footer: Sources card footer row in en/de/fr/ru and both themes; no wrap, same segment places for 0/1/10/1000/12345, labels then counts dropped by width, exact tooltips and names; private Xvfb, no network"),
        Root("post-bookmark-transitions", "MEOWCHAN_POST_BOOKMARK_TRANSITIONS_ONLY"),
        Root("post-bookmark-locale", "MEOWCHAN_POST_BOOKMARK_LOCALE_ONLY"),
        Root("post-bookmark-comparison", "MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY"),
        Root("post-bookmarks", "MEOWCHAN_POST_BOOKMARKS_ONLY"),
        Root("command-contrast", "MEOWCHAN_COMMAND_CONTRAST_ONLY"),
        Root("hover-contrast", "MEOWCHAN_HOVER_CONTRAST_ONLY", "hover contrast: footer navigation hover/selected, work-view selector and drop-down, Tools menu, columns context menu and a settings drop-down in Light and Dark; rendered text 4.5:1, state boundaries 3:1"),
        Root("action-dynamic", "MEOWCHAN_ACTION_DYNAMIC_ONLY"),
        Root("action-overflow", "MEOWCHAN_ACTION_OVERFLOW_ONLY") with { NightlySkip = Provenance },
        Root("download-search", "MEOWCHAN_DOWNLOAD_SEARCH_ONLY"),
        Root("settings-footer", "MEOWCHAN_SETTINGS_FOOTER_ONLY"),
        Root("ui-refinement", "MEOWCHAN_UI_REFINEMENT_ONLY"),
        Root("plugin-menu-language", "MEOWCHAN_PLUGIN_MENU_LANGUAGE_ONLY", "plugin menu language: passive real menu reload, transient status/discard cancellation and raw draft retention"),
        Root("plugin-settings-language", "MEOWCHAN_PLUGIN_SETTINGS_LANGUAGE_ONLY", "plugin settings language: UI-thread editor reconstruction, raw drafts and provider messages retained; no provider activation"),
        Root("language-switch-footer", "MEOWCHAN_LANGUAGE_SWITCH_FOOTER_ONLY", "language switch footer: passive EN/ES/FR/RU/EN mode selector and command-label refresh; no URL/source activation"),
        Root("preview-languages", "MEOWCHAN_PREVIEW_LANGUAGES_ONLY", "bundled preview packs (ES/FR/RU/zh-Hans and later additions): embedded load, settings commit/live switch, raw drafts, screenshots and CJK font/.notdef evidence; no whole-UI/native linguistic claim"),
        Root("window-chrome", "MEOWCHAN_WINDOW_CHROME_ONLY", "app chrome: isolated native geometry and right-edge pixels, own captions of About, Settings, ratings manager, rating prompt (Dialog variant), modeless bookmarks and ownerless startup error with square-corner request and Escape/caption close, modal lifetime; platform/scales as recorded"),
        Root("logo-theme", "MEOWCHAN_LOGO_THEME_ONLY", "logo by theme: orange cat in Light, grey cat in Dark for the main window icon, About icon, a plain dialog and a standalone tray icon, live Light/Dark/System switches and new windows; the header shows one theme-appropriate cat and About keeps both cats side by side, unclipped at 640x480/760x500 and the About minimum; asserted assets and rendered colour, private Xvfb, no native tray host or Windows/macOS claim"),
        Root("shortcut-presentation", "MEOWCHAN_SHORTCUT_PRESENTATION_ONLY"),
        Root("poweruser", "MEOWCHAN_POWERUSER_ONLY"),
        // Work views (AP5/WV0): page x view x theme x language x size captures, index.html matrix and the reduced-shortcut probe.
        Root("work-views", "MEOWCHAN_WORK_VIEWS_ONLY", env => "work views: offline showcase fixture, "
            + (WorkViewPageSelector(env) ?? "every page") + " in Barebones/Simple/Advanced, "
            + (env("MEOWCHAN_WORK_VIEWS_MATRIX") ?? (WorkViewPageSelector(env) == null ? "full" : "reduced"))
            + " theme/language/size matrix with index.html, reduced-toolbar shortcut probe; managed input only, no network"
            + (WorkViewPageSelector(env) is null or "booru"
                ? "; Booru: fake ports only, real shell navigation, result and watcher controls/captures" : "")),
        .. WorkViewPages.Select(page => Alternative("work-views-" + page, WorkViewSelector(page), "MEOWCHAN_WORK_VIEWS_ONLY"))
    ];


    internal static string WorkViewSelector(string page) => "MEOWCHAN_WORK_VIEWS_" + page.ToUpperInvariant() + "_ONLY";

    // Only alternative reader runs are isolated here; observational debug selectors
    // keep the historical unsplit reader mode. Validation rejects competing alternatives.
    internal static string? ReaderAlternative(Func<string, string?> env) =>
        All.FirstOrDefault(entry => entry.Group == "--reader-ux" && env(entry.Selector) == "1")?.Id;

    private static string? WorkViewPageSelector(Func<string, string?> env) =>
        WorkViewPages.FirstOrDefault(page => env(WorkViewSelector(page)) == "1");

    // The standalone run of one entry: its command-line arguments and NAME=value environment
    // (selectors plus NightlyEnvironment), and the inherited NightlySkip reason if any.
    internal static (string[] Args, string[] Environment, string? Skip) Invocation(SmokeScenario entry)
    {
        switch (entry.Kind)
        {
            case SmokeScenarioKind.Mode:
                return (entry.Selector.Length == 0 ? [] : [entry.Selector], entry.NightlyEnvironment, entry.NightlySkip);
            case SmokeScenarioKind.Root:
            case SmokeScenarioKind.Branch:
                string selector = entry.Selector.Contains('=') ? entry.Selector : entry.Selector + "=1";
                return ([], [selector, .. entry.NightlyEnvironment], entry.NightlySkip);
            default:
                SmokeScenario parent = All.Single(candidate => candidate.Selector == entry.Requires[0]);
                (string[] args, string[] environment, string? skip) = Invocation(parent);
                return (args, [.. environment, entry.Selector + "=1", .. entry.NightlyEnvironment],
                    entry.NightlySkip ?? (parent.NightlySkipCoversNested ? skip : null));
        }
    }

    private static SmokeScenario Mode(string id, string pass) =>
        new(id, SmokeScenarioKind.Mode, id == SmokeSelectors.NormalMode ? "" : "--" + id, _ => pass, []);

    private static SmokeScenario Root(string id, string selector, string pass = NormalPass) => Root(id, selector, _ => pass);

    private static SmokeScenario Root(string id, string selector, Func<Func<string, string?>, string> pass) =>
        new(id, SmokeScenarioKind.Root, selector, pass, []);

    // Branch selectors have never had a claim of their own and report the normal scene's.
    private static SmokeScenario Branch(string id, string selector) =>
        new(id, SmokeScenarioKind.Branch, selector, _ => NormalPass, []);

    private static SmokeScenario Nested(string id, string selector, params string[] requires) =>
        new(id, SmokeScenarioKind.Nested, selector, null, requires);

    // A nested selector that picks one alternative run of its single parent.
    private static SmokeScenario Alternative(string id, string selector, string parent) =>
        Nested(id, selector, parent) with { Group = parent };
}
