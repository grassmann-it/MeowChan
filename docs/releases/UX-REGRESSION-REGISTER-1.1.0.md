# UX regression register — 1.1.0 final Astra review

Recorded 2026-11-16 from user dogfooding. This is a review checklist, not a claim of release acceptance.

## Evidence and version discipline

The reported Archive screenshots and running desktop were traced to local Debug build
`69c088a47b58811ef0ac02fb5d87cb241ca727df` in `/home/tomka/Desktop/oChan`.
The active candidate run `43953373716` tests `5ca63aa3d645ee1d113e498b5fc3e4dc3f252f26`.
Do not classify an old-build screenshot as a regression of the newer candidate without reproduction.
Source-level fixes or contract tests alone do not prove native mouse/keyboard delivery or rendered geometry.

## Classes and concrete reports

### UX-01 — Excess unused space / controls do not use their allocated width

- **Report:** Archive List/Table occupies only a small area at the left of its row, with a large gap before the overflow button (⋮).
- **Expected:** The joined pair shares the available width up to the overflow slot, with intentional normal spacing. Preserve readable labels, joined borders and an accessible overflow target.
- **Source status:** Claude's `5c9f6150e` implements `ActionToolBar.Stretch`; included in the current candidate, absent from the user's running old build. Native visual verification on the final build remains required.
- **Cross-view review:** Archive, reader's Saved media tab, Media, Tagging and comparable segmented controls. Inspect full width, half-screen and narrow windows, not only a fixed smoke size; English and long German labels; light/dark themes and display scaling.
- **Regression evidence:** Measure actual segment/group/overflow bounds after resizing in both directions. Overflow decisions must still use natural sizes; no oscillation, clipping, inaccessible actions or unwanted line breaks.
- **Do not overcorrect:** Whitespace that separates unrelated groups is useful. Fill the control's intended area, not every blank pixel of the window.

### UX-02 — Visible view without an initialized selection

- **Report:** Clicking a thread while the media sidebar is already open leaves “Select a post to see its media here”, although the thread has loaded posts and media. “Enlarge focused media” should apply without a second selection click.
- **Expected:** Establish an initial post selection when opening a thread. An explicit jump or valid restored reading position has priority; otherwise select the first visible post. Display its first applicable medium and apply the existing focused-media enlargement mode.
- **Status:** Dedicated current-source investigation/fix in progress; do not mark resolved from old screenshots or unrelated thumbnail fixes.
- **Cross-view review:** Archive embedded reader, large reader, sidebar image/video, filtered and paged posts, reopening a thread and switching threads quickly.
- **Regression evidence:** Real thread open → selected card → correct sidebar medium → enlargement. Cover empty thread, first post without media, filtered initial post, saved position, explicit post jump, cancellation/stale loads and live updates. Selection must not steal keyboard focus from unrelated controls or unexpectedly move a live reader's scroll position.
- **Relationship to UX-01:** A large empty media panel caused by missing selection is a state-initialization problem, not simply a margin problem. Legitimate no-media states need an honest explanation, never a stale previous image.

### UX-03 — Commands disappear when focus crosses media/reader boundaries

- **Report:** R does not open rating/tagging when a medium is focused in Reader/Archive, despite F working.
- **Expected:** The focused reader medium resolves consistently for rating/tagging and other applicable reader/media commands, including sidebar and enlarged reader. A full gallery uses its own displayed-entry context.
- **Source status:** `87f1a4c3` routes rating before ListBox type-ahead; subsequent `30019e607` and `ccb3be5e5` cover further scoped sidebar routes. These fixes are absent from the reported running build. No additional latest-source defect has yet been reproduced.
- **Cross-view review:** Post card/body selection, attachment, sidebar picture, thumbnail strip, inline video controls, gallery and native video overlay; mouse focus and Tab traversal; default and rebound shortcuts.
- **Regression evidence:** Deliver actual keys through the focused control, not directly to the handler. Verify exactly one action and the correct medium. TextBox/ComboBox, popup, capture mode, Enter/Space activation and foreign windows retain their own keys. After Escape closes tagging, navigation must work again and focus must return sensibly.

### UX-04 — Passive state arrives late or is never repainted

- **Report:** Thread favorite status takes a long time to load.
- **Current-source finding:** Thread favorites are hydrated after plugin/registry startup although the catalog can already be readable. Existing Archive rows can miss a subsequently published snapshot until another refresh.
- **Status:** Dedicated bounded fix and regression tests in progress.
- **Expected:** Known favorite state should be available with readable thread data where feasible; loading, unavailable/error and off are distinct states. A newer snapshot updates visible rows/detail without disk I/O on the UI thread.
- **Cross-view review:** Thread list/table, reader header, favorite overview and any other passive metadata badges. Slow/failed plugin startup must not indefinitely block unrelated local state.
- **Regression evidence:** Delay startup deliberately, then publish the snapshot; assert visible state catches up. Cover favorite writes during load, failure/retry, host/profile rebinding and disposal. Old completions must never paint a newly opened profile.

## Final Astra pass and acceptance record

For each class, record the final commit/build identity, surface, window dimensions/scaling, theme/language,
mouse or keyboard route, screenshot/test evidence, and outcome. Use full-screen, half-screen and narrow
sizes, including resize transitions; do not substitute source searches for GUI evidence.

Classify findings as release blocker, safe 1.1.0 quick fix, or explicitly deferred 1.1.1/1.2.0/2.0.0 work.
Apply a demonstrated fix to equivalent affected surfaces, but do not mechanically apply one handler or
layout rule to contexts with different ownership. Keep unresolved items visible. A green CI run and this
register do not replace the owner's final review; publication is not authorized, only a draft afterward.
