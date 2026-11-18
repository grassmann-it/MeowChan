# UX/UI release follow-ups — 17 November 2026

This is the short backlog for issues found while dogfooding the release candidate. Items are
kept here even when a focused fix lands, so the next visual review can check the whole class
rather than only one screen.

## Package 7A evidence ledger audit — 18 November 2026

**Scope:** documentation-only audit on `origin/goal/gesamtprogramm-profile-a` at
`866ad8ea24a09d57e9e8e5e3452d4de6beaa100a` in the dedicated Package 7A worktree.
No product code, CI configuration, or DesktopSmoke source was changed.

**Evidence rule:** a status below is based only on repository files, committed
history/ancestry, and the referenced PR/commit. Historical test counts are not a
fresh current-head verdict. “Implementation evidence” is deliberately separate
from native-platform or release acceptance.

### Verified implementation records

| Ledger item | Evidence present in this checkout | Status / remaining gate |
| --- | --- | --- |
| UX-LEDGER-08 — tagging selection revisit | `docs/development/ux-ledger-08-tagging-selection-revisit-20261117.md`; inherited fix `548b25ae3`; PR #23 commits `7a313b2e0`, `9a80fc07f`; all are ancestors of the audited base. | Implementation and targeted lifecycle evidence are recorded. This is not a repository-wide, Windows, or macOS release verdict. |
| UX-LEDGER-09 — Settings content-column header | Central ledger row for PR #24 / `8187f6a16`; commit is an ancestor of the audited base. | Merged implementation is evidenced. Native width/language acceptance remains a GUI-sweep gate. |
| UX-LEDGER-10 — Rate-and-tag initial focus | `docs/development/ux-ledger-10-r-focus-input-20261117.md`; PR #33 merge `0e208fe98`; the document records the scoped DesktopTests and Linux Xvfb/XTest evidence. | Targeted implementation evidence is recorded. Windows/macOS, real IME, and full-suite acceptance remain open. |
| UX-LEDGER-11 — clicked autocomplete acceptance | Central ledger rows for PRs #28/#30 (`bb4c131ca`, `b849f94fd`); both commits are ancestors of the audited base. | Managed and Linux-input evidence is recorded. Native Windows/macOS and real-IME acceptance is explicitly still open. |
| UX-LEDGER-12 — favorite/rating row layout | Central ledger row for PR #22 / `603125d76`; commit is an ancestor of the audited base. | Implementation is evidenced; a visual/native acceptance pass is not inferred from ancestry alone. |
| UX-LEDGER-13 — one-flow quick-tag scrolling | `docs/development/ux-ledger-13-quicktag-scroll-flow-20261117.md`; PR #42 / `da6970275`; targeted DesktopTests are recorded there. | Deterministic layout evidence is recorded. Native wheel/touch, screen-reader, theme/contrast, and full-window review remain unverified. |
| UX-LEDGER-14 — medium counter beside Post/Tags | `docs/development/ux-ledger-14-medium-counter-header-20261117.md`; PR #25 / `21c0d5cb7` and PR #27 / `7ffeab1ce`; commits are ancestors of the audited base. | Placement and focused contract evidence are recorded; the broader native matrix remains a release-sweep gate. |
| UX-LEDGER-19 — point-of-use shortcut discovery | `docs/development/ux-ledger-19-discovery-20261117.md` and `docs/development/evidence/ux-ledger-19-validation-20261117.txt`; PR #65 / `1c28b8054`; normal-analyzer targeted validation is recorded. | Targeted validation is evidenced. The document explicitly leaves UX-LEDGER-18 routing debt and native macOS/Windows, screen-reader, and visual tooltip sizing open. |

### Package 7A capture that is **not** promoted to verified status

The earlier worker capture commit `5d806ba93` (branch
`codex/ux-ui-ledger-7a-20261117`) is not an ancestor of the audited base. Its
UX-LEDGER-30…37 rows cite coordination files that are not present in this
checkout. Therefore this audit does **not** convert those reports into verified
findings and does **not** mark any of them fixed or finished. If they remain in
the handover, they are open review candidates only until the referenced artifact,
a reproducible observation, and a current status/PR/commit are attached.

| Candidate IDs | Honest status | Required evidence before closure |
| --- | --- | --- |
| UX-LEDGER-30…37 | **Open / unverified in this checkout; no implementation claim.** | Preserve the source screenshot/report or reproduce on the current base; attach the affected surface, expected behavior, and a current PR/commit or an explicit rejected/deferred decision. |

### Open gate items carried forward

| Gate item | Repository evidence | Status |
| --- | --- | --- |
| FC-02 — Tagging right-click target/focus | `docs/development/ux-ui-release-followups-20261117.md` focus-click closeout; `docs/development/ux-ledger-06-click-contracts.md` records the explicit skip and its two-case theory. | **Open / skipped.** Selection-revisit coverage does not prove page-local right-click target resolution. |
| FC-04 — Gallery filmstrip right-click navigation/menu lifetime | Same focus-click closeout and `ux-ledger-06-click-contracts.md`; asynchronous `MoveGalleryAsync`/navigation completion is the documented blocker. | **Blocked / skipped.** No closure claim. |
| UX-LEDGER-11 native acceptance | Current central ledger says the implementation is present but native 1099/1100 and Ubuntu GUI evidence remain required. | **Open.** |
| O2-04 large-reader detach safety | Current central ledger records `OpenLargeReader` can leave the page half-detached when wrapping throws. | **Open.** |
| Release gate | Current central ledger explicitly says no complete all-platform CI result was verified in the audit. | **Open; no release PASS.** |
| UX-LEDGER-18 shortcut rebinding routing | `docs/development/ux-ledger-19-discovery-20261117.md` explicitly retains old quick-tag paths after rebinding as an adjacent defect. | **Open; not closed by UX-LEDGER-19.** |

This addendum is a ledger audit, not a product or CI change. It intentionally
records unresolved gates and evidence gaps instead of inferring “done” from a
branch name, a worker report, or an inherited test count.

## Merged implementation status — 17 November 2026

Truth-sync base: `f48ccf8b1` on `origin/goal/gesamtprogramm-profile-a`. The merged
first-parent diffs and test sources establish the delivered scopes below, not a fresh
test run or release-acceptance verdict. Earlier validation counts are historical only.
Ledger IDs were reused across work streams; use the scope and PR/commit together.
The earlier review lists below remain historical snapshots. This section supersedes
only the specific behavior covered here, not the remaining backlog or release gates.

| Ledger ID | Implementation PR | Delivered scope |
| --- | --- | --- |
| UX-LEDGER-01 | [#20](https://github.com/grassmann-it/MeowChan/pull/20) | Shared toolbar end docking and spacing, compact Archive List/Table controls, empty-overflow suppression |
| UX-LEDGER-02 | [#19](https://github.com/grassmann-it/MeowChan/pull/19), [#24](https://github.com/grassmann-it/MeowChan/pull/24) (`8187f6a16`) | Wrapping help links and no horizontal help/tooltip scrolling; #24 places search and More in the actual shared content column above the scrolling form, replacing #19's separate header grid |
| UX-LEDGER-03 | [#17](https://github.com/grassmann-it/MeowChan/pull/17) | Persistent reader-toolbar Media sidebar On/Off toggle; stable header/full-height layout; unchanged default scope |
| UX-LEDGER-04 | [#14](https://github.com/grassmann-it/MeowChan/pull/14) | Activity/status/name plugin-card order, ten-second activity hold, uniform effective Sources/Filters/Running/Files footer including inactive zeros |
| UX-LEDGER-05 | [#16](https://github.com/grassmann-it/MeowChan/pull/16) | Booru preview viewer, current-page navigation and shared per-image selection, explicit displayed-original download |
| UX-LEDGER-06 | [#18](https://github.com/grassmann-it/MeowChan/pull/18) | Downloads/Archive/Media pointer-target selection and focus; Media sidebar preview-only single-click; source/wiring contracts (remaining skips tracked below) |
| UX-LEDGER-07 (CI-LEDGER-07) | [#15](https://github.com/grassmann-it/MeowChan/pull/15) | Independent smoke modes continue after failures; aggregate jobs and summaries retain failures and unsafe-cleanup/interruption blocking; no product behavior changes |
| UX-LEDGER-08 | `d00eb27f9`, `3c946ef77` | Continue Reading is reachable over Gallery and opening a thread restores its valid saved post/anchor; a missing saved post resolves to the next captured post, or the last captured post when no later one exists; Shell/Reader entry coverage added |
| UX-LEDGER-09 | `f4378faa3` | Header network/disk statistics activation closes Gallery/large Reader before opening Statistics; contract coverage added |
| UX-LEDGER-10 | `d00eb27f9` | Continue Reading remains enabled and usable while Gallery/large Reader covers the workspace; Gallery closes before resume is applied |
| UX-LEDGER-11 | `740d0d565` | Media selection context targets, multi-selection shortcut feedback/rating, hand-cursor click contract, and 1099/1100 selection-line fitting; test coverage present; native GUI validation pending |
| Rating-row favorite layout | [#22](https://github.com/grassmann-it/MeowChan/pull/22) (`603125d76`) | Favorite spans the five-star row width (excluding the clear gutter), with a larger glyph/hit area and caption below; shared Gallery/Rate and tag control |
| Tagging selection revisit (also called UX-LEDGER-08) | [#23](https://github.com/grassmann-it/MeowChan/pull/23) (`7a313b2e0`, `9a80fc07f`) | Adds twenty list/grid single/multi-selection revisit cycles plus work-view, scope, group, filter and clear checks in `TaggingPageChecks.cs`; retains the inherited clear-before-bind fix, not a new product fix or right-click closeout |
| UX-LEDGER-14 medium counter | [#25](https://github.com/grassmann-it/MeowChan/pull/25) (`21c0d5cb7`), [#27](https://github.com/grassmann-it/MeowChan/pull/27) (`7ffeab1ce`) | Moves the same live counter beside Post/Tags; adds localized text, accessible-name and polite-live-region assertions before and after navigation in `GalleryMediaTagChecks.cs` |
| Focus-click closeout | [#26](https://github.com/grassmann-it/MeowChan/pull/26) (`f159827dd`); `cde745dfc` | Enables/fixes Reader single-click and Gallery viewport focus ordering; later Media fix restores inline target selection/focus before pointer/keyboard menus. FC-02 and FC-04 remain skipped, not closed |
| Autocomplete click acceptance (also called UX-LEDGER-11) | [#28](https://github.com/grassmann-it/MeowChan/pull/28) (`bb4c131ca`), [#30](https://github.com/grassmann-it/MeowChan/pull/30) (`b849f94fd`) | Restricts quick-tag pointer focus repair to editor visuals so captured popup clicks commit the chosen suggestion; adds `TagAutocompleteCommitChecks.cs` and editor-only focus-repair assertions in `QuickTagSurfaceTests.cs` |
| Help/changelog | [#21](https://github.com/grassmann-it/MeowChan/pull/21) (`0198ed884`) | User instructions and release notes for toolbar, Settings/help, Reader sidebar, plugin cards, Booru preview and click contracts; later merged changes are documented by this truth-sync |

The gallery behavior audited by UX-LEDGER-06 is **double-click toggles window
fullscreen**, not the superseded Fit/1:1 description; **0** selects Fit and **1**
selects 1:1. Its right-click selection/focus fixes cover Downloads, Archive and
Media. #26 subsequently fixes Reader activation and Gallery viewport focus;
Tagging right-click and Gallery filmstrip targeting remain open below. The Booru
viewer operates on already-fetched previews on the current result page: selecting or browsing never downloads originals, and its download
command targets only the displayed post. This does not close the whole earlier
Booru pipeline backlog.

User-facing instructions are in [Help](../Help.md#selection-and-shortcuts),
[reader sidebar help](../Help.md#reader-media-sidebar-and-inline-video),
[Booru viewer help](../Help.md#browse-result-previews) and the
[German Booru guide](../user-guide/danbooru.md#treffer-in-der-vorschau-durchsehen).
The original documentation follow-up merged as #21 after the seven implementation PRs.
Full all-platform release acceptance remains a separate gate; CI orchestration
hardening does not itself establish a green sweep.

## Focus-click contract skip closeout

Original audit base: `740d0d565b9c68a0db90c579d6df45a27dbd3823`; current
status checked against `f48ccf8b1`. #26 closed two skips, **not all four**. This section supersedes
the protected-scope status above for the four skipped declarations in
`tests/MeowChan.DesktopTests/FocusClickContractTests.cs`. These are source/wiring
contracts, **not native pointer acceptance**. Skipped remains blocked, not passing.

| Follow-up | Contract / verdict | Evidence and ownership |
| --- | --- | --- |
| FC-01 — Reader-Media-Activation | **Enabled, fixed:** sidebar single-click must not open Gallery for any Reader preference. | With the skip removed, `ReaderSidebarSingleClickMustNotOpenGalleryForAnyReaderPreference` failed because `ArchivePage.InlineVideo.cs:OnMediaSidebarPictureActivated` called `OpenMediaSidebarMedia()` for the Gallery preference. Removed that branch; offered inline video still toggles, and `ArchivePage.MediaSidebar.cs` still wires `GalleryRequested` to the explicit Gallery open. Cursor now reflects only inline-video activation. Reader owner: native single/double-click, video and Open-button recheck remains required. |
| FC-02 — TaggingPage-Selection | **Open / still skipped**, despite merged #23. | `TaggingRightClickMustSelectAndFocusTargetBeforeComposingMenu` remains a skipped two-case theory in `FocusClickContractTests.cs`. [#23](https://github.com/grassmann-it/MeowChan/pull/23) adds lifecycle coverage only; its diff changes no Tagging implementation or focus-click skip. The required page-local target/focus wiring is not established. Selection revisit success does not prove right-click correctness; select an outside target, preserve an inside multi-selection and focus before composing in both views, then enable/validate this contract. |
| FC-03 — Gallery-Context-Core / viewport | **Enabled, fixed:** reclaim surface focus before opening the menu. | `GalleryViewportRightClickMustFocusSurfaceBeforeOpeningMenu` failed its ordering assertion: `GalleryViewer.MediaMenu.cs:OnMediaContextRequested` went straight to `ShowMediaMenu()`. It now calls `FocusGallerySurface()` first, after the existing handled/entry/native-text guards, so the presenter captures surface return focus rather than unrelated toolbar focus. Gallery owner: native image/video and menu-dismissal focus acceptance still required. |
| FC-04 — Gallery-Context-Core / filmstrip | **Blocked:** right-click must select/navigate to the target, not merely retarget its menu. | `GalleryFilmstripRightClickMustSelectTargetNotOnlyRetargetTheMenu` fails: `GalleryViewer.Filmstrip.cs:OnFilmstripTileContextRequested` calls `ShowMediaMenu(entry)` for an unselected tile, never `OnFilmstripTileActivated(index)`. Activation raises `MoveRequested`; `Desktop/Shell/MainWindow.Gallery.cs` starts asynchronous `MoveGalleryAsync`. `GalleryViewer.cs:ShowMediaAsync` changes `_navigationId` and calls `InvalidateMediaMenu()`, while `ShowMediaMenu` binds validity to that navigation ID. Merely adding the activation call can immediately invalidate the menu or open it before the new medium is committed. Gallery context/navigation owner must arrange a completion-bound target menu, cancel stale requests on rapid navigation/close/failure, preserve current-tile behavior and reject non-media/more tiles; then remove the skip and add navigation/menu-lifetime coverage. |

### Historical validation of #26 (not rerun for this truth-sync)

- An isolated run of the two newly enabled tests completed: **2 passed / 0 failed /
  0 skipped**. `git diff --check` also passed.
- Removing all four skip attributes produced **13 passed / 7 failed / 0 skipped**
  in `FocusClickContractTests`: the four declarations contributed five failures
  (Tagging is a two-case theory); two already-enabled Media right-click cases
  also failed. The Reader cursor-only adjustment was already present for this
  diagnostic run; neither the Reader click handler nor Gallery handler had been fixed.
- After the fixes, the combined `FocusClickContractTests`,
  `MediaViewsDeadAffordanceTests` and `ReaderMediaSidebarTests` run completed:
  **78 passed / 2 failed / 2 skipped, 82 total**. The newly enabled Reader and Gallery
  viewport tests pass; both remaining skip declarations have explicit blockers above
  (xUnit reports the skipped theory as one result, not its two data cases).
- At that audit base, the two failures were the already-enabled
  `MediaRightClickPreservesOnlyASelectionContainingTheTargetAndFocusesBeforeMenu`
  cases. They reproduce before these fixes: the base moved selection into
  `MediaPage.cs:TargetForMenu` and no longer contains the asserted inline
  selection/focus sequence. Media owner must reconcile the helper and the focus
  contract. **Subsequently delivered:** `cde745dfc` adds the asserted inline
  selection/focus sequence before Media pointer and keyboard menus. The old failures
  are not a current-head verdict; no fresh run is claimed here.
- Full affected project command:
  `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -m:1 --blame-hang-timeout 3m`.
  The runner did not finish within the outer **600-second limit** and was terminated;
  no green full-project verdict is claimed. Before timeout it reported nine failures:
  the two Media cases above, two `ReaderWorkViewTests`,
  `ArchiveSegmentStretchTests`, `MediaBooruOverflowRegistrationTests`,
  `Hygiene.FireAndForgetHygieneTests.NoNewDiscardedTasks`, and two DesignLint rules
  (`spacing-off-grid`, `color-code`). These other failures reference untouched files.
- Targeted reproduction command:
  `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~FocusClickContractTests|FullyQualifiedName~MediaViewsDeadAffordanceTests|FullyQualifiedName~ReaderMediaSidebarTests'`.

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
- **Continue-reading entry point (UX-LEDGER-08):** implemented for the Archive large-reader
  entry path and covered-view resume routing. Native full entry-path coverage still belongs to
  the release sweep: first/middle/final, invalid/filtered positions, rapid thread switching,
  and sidebar-open media selection must be checked before release.

## Later (1.2+ candidate)

- Shared query language and saved/recent searches on Media (and other collection pages): `and`,
  `or`, `not`, tag/title/source fields, and a compact combined search interface.
- **Header View menu icon:** the View/work-view menu entry should carry the same eye icon as
  `workspace.view-cycle`, so the menu is scannable and consistent with the header affordance.
- **Settings search alignment — delivered #24 (`8187f6a16`), following #19:** search and
  the three-dot More menu now share the actual content column above the form. Native
  width/language acceptance remains in the GUI sweep, not an unimplemented layout item.
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
  the already-open gallery surface toggles window fullscreen (including video), as guarded
  by `GalleryImageAndVideoDoubleClickRequestFullscreenWithoutChangingFitOrZoom`.
  F11/the explicit fullscreen command also change window fullscreen; **0** selects Fit
  and **1** selects 1:1. The broader surface-by-surface activation sweep remains open.
- **Tag autocomplete acceptance — implementation and coverage delivered:** the earlier Enter
  fix is recorded below; #28 (`bb4c131ca`) fixes clicked suggestions being lost during
  focus reclaim and #30 (`b849f94fd`) guards editor-only repair. The new smoke covers
  click/highlight-click/Enter, middle/quoted tokens, Tab then Enter, comma behavior and
  a composition guard. Native Windows/macOS and real IME acceptance remain open: the
  smoke uses Linux XTest clicks, routed keys and simulated preedit, not that certification.
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

### Newly captured follow-ups (17 November 2026)

- **UX-LEDGER-09 Header statistics routing:** implementation is present and contract-tested. The
  full native matrix (mouse, keyboard, accessible name, every work-view layout, and the normal
  return path) remains part of the release sweep.
- **UX-LEDGER-10 Continue Reading reachability in covered views:** implementation is present and
  contract-tested for Gallery/large Reader coverage. The full native matrix (mouse, Enter/Space,
  menu/Shift+F10, app menu and shortcut) remains part of the release sweep.
- **Ledger audit rule:** every newly reported click/focus/routing failure gets a stable ledger ID
  before implementation, with its reproduction surface, expected behavior, fallback behavior and
  a native smoke or contract test. Before release, compare the conversation handover against this
  ledger and explicitly mark each item fixed, deferred or rejected; do not rely on memory.

### Open

- **UX-LEDGER-11 native validation:** implementation is now on the release branch in
  `740d0d565`; its 83-test claim is a prior hand-over report, not revalidated here.
  `cde745dfc` subsequently restores Media menu focus ordering. The Ubuntu GUI sweep and native
  1099/1100 evidence still have to pass before this item is fully closed.
- **O2-04** `OpenLargeReader` detaches before wrapping; any throw leaves the page half-detached.
- Smokes red at hand-over: `settings-category` ("accessible category selection disagrees with
  page"), `quick-tag-native-typing` (one repeat under load), `reader-ux` (auto-hide; upstream
  fixes merged, not rerun). `private-tags` fails at an old thread-menu check.
- Release gate: no complete all-platform CI result was verified in this diff/test-source
  audit. #15 hardens orchestration but cannot establish a green current-head release sweep.
- Historical review claims and red smoke reports above are retained as hand-over evidence,
  not fresh verdicts. Unchanged native routing, loading, Booru pipeline, video/focus and
  `O2-04` follow-ups remain open where no closing diff/test evidence was verified.
