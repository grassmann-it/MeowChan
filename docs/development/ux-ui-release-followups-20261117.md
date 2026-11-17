# UX/UI release follow-ups — 17 November 2026

This is the short backlog for issues found while dogfooding the release candidate. Items are
kept here even when a focused fix lands, so the next visual review can check the whole class
rather than only one screen.

## UX-LEDGER 01–07 implementation status — 17 November 2026

The seven follow-up PRs are listed below. Their linked descriptions and diffs record
implementation and validation scope; a PR link is not a release-acceptance verdict.
The earlier review lists below remain historical snapshots. This section supersedes
only the specific behavior covered here, not the remaining backlog or release gates.

| Ledger ID | Implementation PR | Delivered scope |
| --- | --- | --- |
| UX-LEDGER-01 | [#20](https://github.com/grassmann-it/MeowChan/pull/20) | Shared toolbar end docking and spacing, compact Archive List/Table controls, empty-overflow suppression |
| UX-LEDGER-02 | [#19](https://github.com/grassmann-it/MeowChan/pull/19) | Settings search/More aligned to the content column; wrapping help links and no horizontal help/tooltip scrolling |
| UX-LEDGER-03 | [#17](https://github.com/grassmann-it/MeowChan/pull/17) | Persistent reader-toolbar Media sidebar On/Off toggle; stable header/full-height layout; unchanged default scope |
| UX-LEDGER-04 | [#14](https://github.com/grassmann-it/MeowChan/pull/14) | Activity/status/name plugin-card order, ten-second activity hold, uniform effective Sources/Filters/Running/Files footer including inactive zeros |
| UX-LEDGER-05 | [#16](https://github.com/grassmann-it/MeowChan/pull/16) | Booru preview viewer, current-page navigation and shared per-image selection, explicit displayed-original download |
| UX-LEDGER-06 | [#18](https://github.com/grassmann-it/MeowChan/pull/18) | Downloads/Archive/Media pointer-target selection and focus; Media sidebar preview-only single-click; interaction guards with explicit protected-scope skips |
| UX-LEDGER-07 (CI-LEDGER-07) | [#15](https://github.com/grassmann-it/MeowChan/pull/15) | Independent smoke modes continue after failures; aggregate jobs and summaries retain failures and unsafe-cleanup/interruption blocking; no product behavior changes |

The gallery behavior audited by UX-LEDGER-06 is **double-click toggles window
fullscreen**, not the older Fit/1:1 description below; **0** selects Fit and **1**
selects 1:1. Its right-click selection/focus fixes cover Downloads, Archive and
Media, not the protected Reader activation, Tagging selection or Gallery context
implementations. The Booru viewer operates on already-fetched previews on the current
result page: selecting or browsing never downloads originals, and its download
command targets only the displayed post. This does not close the whole earlier
Booru pipeline backlog.

User-facing instructions are in [Help](../Help.md#selection-and-shortcuts),
[reader sidebar help](../Help.md#reader-media-sidebar-and-inline-video),
[Booru viewer help](../Help.md#browse-result-previews) and the
[German Booru guide](../user-guide/danbooru.md#treffer-in-der-vorschau-durchsehen).
The documentation follow-up is intended to merge after all seven implementation PRs.
Full all-platform release acceptance remains a separate gate; CI orchestration
hardening does not itself establish a green sweep.

## Fixed in this release candidate

- **Quick tag input focus (breaking):** when Rate and tag opened with the rating row focused,
  clicking the tag editor could leave keyboard input on the stars. The editor now explicitly
  claims focus after a left click while preserving the native caret gesture.
- **Rating clear affordance:** the old generic `×` icon was ambiguous. The action now uses a
  struck-through star; its accessible name and shortcut remain “Remove rating”.

## Recheck in the next GUI sweep

- **Ambiguous icon semantics:** every icon-only action needs a tooltip/name and must be checked
  for “close”, “clear”, “remove”, and “reset” being visually distinguishable.
- **Empty-space/layout class:** List/Table toggles and archive/media toolbars should consume
  available width consistently; verify at half, normal, and full-screen sizes.
- **Reader/sidebar command routing:** rating, favorite, tag, and navigation shortcuts must reach
  the reader when a media sidebar or thumbnail has focus, without stealing text-entry keys.
- **Shortcut scope parity:** when a media sidebar or reader media has focus, the user should be
  able to request gallery-scoped media actions explicitly (for example `Alt+<gallery shortcut>`);
  document and test the modifier policy so reader commands and gallery commands never conflict.
- **Reader/sidebar rating-entry regression:** with an Archive/Media sidebar item focused, `R` must
  still open the Tagging/Rating surface; test the same routing from Downloads and the reader.
- **Tagging editor keyboard regression (breaking):** the tag input must accept typed text after
  opening the surface, and quick-tag number/letter shortcuts must work without preventing normal
  typing, caret movement, or IME/text-box input.
- **Entry-point state:** opening a thread or reader from Archive/Downloads should select and show
  the first post/media immediately; an explicit enlarge action should enlarge that selected item.
- **Bookmark affordance gap:** the Archive thread-selection menu now exposes the shared Post bookmarks
  manager, while post-level actions remain available from reader/media context menus. Verify that
  Archive, Thread Reader, and Media/sidebar keep one consistent bookmark action (mouse, context menu,
  keyboard, and accessible name); add a direct post action where the focused post is known, or scope
  any persistence/semantics gap explicitly for 1.2.
- **Media loading:** changing posts with the sidebar open must retain the previous image until the
  replacement is ready, or show a low-resolution placeholder; never flash a stale image and then
  leave a permanent grey loading surface.
- **Booru/plugin diagnostics:** current bundled plugin repair must be reachable and actionable;
  stale pinned builds should explain the repair path without silently changing the user pin.
- **Continue-reading entry point (new UX-LEDGER-08):** when a thread has a persisted reading
  position, opening it from Archive/Downloads/Reader entry points must select and reveal the
  corresponding post and its applicable medium instead of always selecting post one. A valid
  saved position on the final post remains on that final post; it must not wrap to the first
  post. If the position is missing, stale, filtered out, or no longer maps to a captured post,
  fall back deterministically to the first visible post. The restore must not steal focus from
  an active text field, overwrite a newer live reading position, or let a stale async load paint
  a previous thread. Add native entry-path coverage for first/middle/final, invalid and empty
  positions, plus rapid thread switching and sidebar-open media selection.

## Later (1.2+ candidate)

- Shared query language and saved/recent searches on Media (and other collection pages): `and`,
  `or`, `not`, tag/title/source fields, and a compact combined search interface.
- **Header View menu icon:** the View/work-view menu entry should carry the same eye icon as
  `workspace.view-cycle`, so the menu is scannable and consistent with the header affordance.
- **Settings search alignment:** the settings search field should align with the content column
  (same left/right edges as the Interface section), not float narrower in the header.
- **Inner-gallery video overlay/focus class:** verify overlay auto-hide/show, hover and pointer
  re-entry, focus loss/return, Escape, fullscreen/fit, play/pause continuity, duration/buffering
  states, and reader/gallery/favorite/rating/tagging shortcuts while a video is focused. A focus
  transition must not silently pause, restart, replace a valid frame with grey, or strand keyboard
  input; capture full- and half-window evidence in the next GUI sweep.
- **Pointer activation semantics:** document and verify single-click, double-click, right-click,
  Enter/Space, and context-menu first-action behavior for threads, posts, rows, tiles, thumbnails,
  sidebars, and dialogs. Single-click selects, double-click opens only where intended, and
  right-click preserves the target selection; differences must be intentional and localized.
  Current gallery contract: double-clicking an archive/media item opens the gallery; double-clicking
  the already-open gallery surface toggles Fit/1:1 (including video), while F11/the explicit
  fullscreen command changes window fullscreen. Do not silently conflate zoom and window fullscreen;
  if dogfooding prefers double-click-to-fullscreen, evaluate it as an explicit design change with a
  replacement gesture for Fit/1:1.
- **Tag autocomplete acceptance (breaking):** pressing Enter on a single matching suggestion must
  accept the complete value (not commit only the typed prefix); with multiple entries, completion
  must replace only the current word and preserve earlier tags. Existing tags with spaces require
  quoted completion and must remain parseable alongside other tags.
- **Video transport focus ownership:** mouse-clicking mute/loop/speed/transport buttons must not
  strand the next gallery shortcut on the clicked toggle; keyboard Tab/Enter activation remains
  native and accessible. Recheck Enter/Space playback immediately after mouse transport actions.

## Final review round — 17 November 2026 (Sonnet discovery → Opus verification → orchestrator)

Stable IDs: `L-F1/L-F2` (fixed list above), `L-R1…L-R10` (recheck list), `L-L1…L-L7` (later list),
in order of appearance. Review reports, screenshots and runtime evidence stay outside the repository
(scratchpad of the review session); the verdicts below are the binding summary.

### Fixed in this round (merged on `goal/gesamtprogramm-profile-a`)

- **U-01 Tag field took no typing (breaking, L-R6/L-F1):** the popover frame marked every
  unhandled key handled, so Avalonia never produced text. Text keys now reach text boxes; letters
  typed on the stars go into the field (TG-02); held opening key does not auto-repeat. Native
  XTest smoke `quick-tag-native-typing`.
- **O1-01 Enter never accepted a highlighted suggestion (breaking, L-L6):** owners' tunnel Enter
  handlers ran before the field. Fields now take Enter on a highlighted row; one real match is
  preselected (`inn` + Enter → `innocent`); completion follows the token under the caret
  (TG-03/TG-04).
- **U-02 Media page jumped on entry:** search width no longer follows the selection line; "Sort"
  is an inline prefix inside the dropdown (`PrefixedComboBox`); ≥ 24 dip group gap; grid keeps its
  view across data re-presents (PF-05).
- **U-03 Booru `sdk-mismatch` route (L-R10, BO-01/02/04/05):** truthful status with the restart
  step, one dialog name "Review bundled repairs", calm confirmed state, Plugins card state.
- **#130 decided:** a pin on a bundled plugin follows the same release (same path/slot/version,
  identical declaration except `PostOrder`, same rights checks as unpinned); otherwise review.
- **L-R3/L-R5/L-L7 class, GA-01/02/03, KB-03:** one `PointerFocusReclaim` rule returns keys to the
  gallery/reader after pointer clicks on chrome; keyboard activation keeps focus.
- **L-R7 / AR-01 / O2-01/02 / DL-01 / RD-01/03/04/05:** enlarge keeps the embedded post and never
  overwrites the reading position; focus returns on close; Esc in the sidebar no longer turns it
  off; honest empty texts.
- **PT-02/PT-03/RD-02:** press in post text selects the post; right-click on text opens the post
  menu (Copy first when text is selected); Ctrl/⌘+F finds in the thread with reader focus.
- **PF-01 (blocker) / PF-03 / AR-04:** a damaged favorites index no longer blocks saved media,
  large reader or gallery; scope switch cancels; attachment stars show loading.
- **ST-02** reset covers every input (coverage test); **ST-06/L-L2/LY-03** app menu ellipsis, View
  eye icon, one title offset; **AR-02/KB-04** archive menu labels, Q on archive surfaces;
  **DL-02/03/04/07** downloads; **AX-02/AX-03** switch and edge-handle contrast; resource-key guard
  test; Help/CHANGELOG/release-notes key tables and "post or medium" key scope (KB-01, RN-*).

### Decided, not built yet

- **L-R4 scope parity:** no modifier prefix; focus decides. Gaps for 1.1.1: Q from reader post
  focus, H from the large-reader sidebar, Shift+Enter opens the gallery at the post's medium, video
  keys in the embedded reader. View keys stay gallery-only.
- **Booru browse-to-download pipeline (1.1.1):** packages B1–B4 done on
  `feature/booru-browse-20261117` (+ `feature/booru-b4-ports-20261117`); B5–B10 open; comments and
  pools need SDK 1.2.

### Open

- **MD-01/PT-01** Media right-click outside a multi-selection acts on the selection; **MD-03** hand
  cursor without single-click action; **KB-06** silent keys with multi-selection; Media counter
  wraps at 1099/1100 with the panel open; Media title offset — unfinished work on
  `wip/media-followups-20261117` (not built).
- **O2-04** `OpenLargeReader` detaches before wrapping; any throw leaves the page half-detached.
- Smokes red at hand-over: `settings-category` ("accessible category selection disagrees with
  page"), `quick-tag-native-typing` (one repeat under load), `reader-ux` (auto-hide; upstream
  fixes merged, not rerun). `private-tags` fails at an old thread-menu check.
- Release gate: no complete all-platform CI run exists for the current head.
