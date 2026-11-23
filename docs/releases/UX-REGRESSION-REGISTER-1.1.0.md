# UX regression register — 1.1.0

Recorded 2026-11-16 from user dogfooding. This is a review checklist, not a claim of release acceptance.

## Evidence and version discipline

The reported Archive screenshots and running desktop were traced to local Debug build
`69c088a47b58811ef0ac02fb5d87cb241ca727df`.
The active candidate run `43953373716` tests `5ca63aa3d645ee1d113e498b5fc3e4dc3f252f26`.
Do not classify an old-build screenshot as a regression of the newer candidate without reproduction.
Source-level fixes or contract tests alone do not prove native mouse/keyboard delivery or rendered geometry.

## Classes and concrete reports

### UX-01 — Excess unused space / controls do not use their allocated width

- **Report:** Archive List/Table occupies only a small area at the left of its row, with a large gap before the overflow button (⋮).
- **Expected:** The joined pair shares the available width up to the overflow slot, with intentional normal spacing. Preserve readable labels, joined borders and an accessible overflow target.
- **Recorded status:** `5c9f6150e` introduced `ActionToolBar.Stretch`. The native capture at `9ac380f15` still shows about 157 px unused at 1440×920. The native retest was pending at this checkpoint.
- **Cross-view review:** Archive, reader's Saved media tab, Media, Tagging and comparable segmented controls. Inspect full width, half-screen and narrow windows, not only a fixed smoke size; English and long German labels; light/dark themes and display scaling.
- **Regression evidence:** Measure actual segment/group/overflow bounds after resizing in both directions. Overflow decisions must still use natural sizes; no oscillation, clipping, inaccessible actions or unwanted line breaks.
- **Do not overcorrect:** Whitespace that separates unrelated groups is useful. Fill the control's intended area, not every blank pixel of the window.

### UX-02 — Visible view without an initialized selection

- **Report:** Clicking a thread while the media sidebar is already open leaves “Select a post to see its media here”, although the thread has loaded posts and media. “Enlarge focused media” should apply without a second selection click.
- **Expected:** Establish an initial post selection when opening a thread. An explicit jump or valid restored reading position has priority; otherwise select the first visible post. Display its first applicable medium and apply the existing focused-media enlargement mode.
- **Status:** Initial row-selection fix integrated as `01d9a70ac`; four Python contracts pass. Three focused C# behavioral tests pass in the integrated selection/menu run (11/11 total); final native verification remains pending.
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
- **Status:** Worker fix `615f58ea6` integrated as `9ac380f15`. Downloads → context-menu Read regression added as `15df10be2`: that route bypassed list loading, previously the only place that populated the page favorite cache. Integrated combined reader/media run passes 56 tests; native entry-path presentation remains pending.
- **Expected:** Known favorite state should be available with readable thread data where feasible; loading, unavailable/error and off are distinct states. A newer snapshot updates visible rows/detail without disk I/O on the UI thread.
- **Cross-view review:** Thread list/table, reader header, favorite overview and any other passive metadata badges. Slow/failed plugin startup must not indefinitely block unrelated local state.
- **Regression evidence:** Delay startup deliberately, then publish the snapshot; assert visible state catches up. Cover favorite writes during load, failure/retry, host/profile rebinding and disposal. Old completions must never paint a newly opened profile.

### UX-05 — Duplicate state indicators on one medium

- **Report:** A favorited reader attachment shows a star beside the file and a second star over its thumbnail.
- **Expected:** One clear favorite affordance/state indicator for that medium in the post; no redundant thumbnail overlay when the adjacent star already supplies it.
- **Status:** Bounded integration/source investigation requested. The screenshot is from the old build. Sidebar badge removal `729341b9d` must not be assumed to cover post thumbnails without checking the actual control path.
- **Cross-view review:** Reader post attachments, inline player poster, media sidebar, gallery filmstrip and archive thread preview. Distinguish thread favorites from media favorites; do not remove an independent, necessary thread indicator.
- **Regression evidence:** Favorite/unfavorite through mouse and keyboard, unloaded/loaded poster, image/video/GIF, selected and unselected card. Verify one visible state indicator, accessible name, and no lost toggle action.

### Integration check — Fixed source versus running application

For every claimed already-fixed defect, verify the fixing commit is an ancestor of the candidate,
inspect the actual current control implementation, and record the running assembly's source identity.
The final review must explicitly distinguish an unmerged patch, a later regression, an incomplete fix
on a sibling surface, and simply running an older checkout. Never resolve that distinction by blindly
merging every worker branch or discarding another worker's changes.

## Acceptance record

For each class, record the final commit/build identity, surface, window dimensions/scaling, theme/language,
mouse or keyboard route, screenshot/test evidence, and outcome. Use full-screen, half-screen and narrow
sizes, including resize transitions; do not substitute source searches for GUI evidence.

Classify findings as release blocker, safe 1.1.0 quick fix, or explicitly deferred 1.1.1/1.2.0/2.0.0 work.
Apply a demonstrated fix to equivalent affected surfaces, but do not mechanically apply one handler or
layout rule to contexts with different ownership. Keep unresolved items visible. A green CI run and this
register do not replace the owner's final review; publication is not authorized, only a draft afterward.

## Related product proposal — unified recent and saved searches

User proposal (not yet an implementation/release-scope decision): reuse Archive's saved-search
experience on Media and potentially other search surfaces; combine recent and saved searches in
one interface, with a configurable recent-search count (suggested default: five).

Suggested design for discussion: one search-history/saved-search entry point with two labeled
sections. Wide layouts may place them side by side; narrow layouts stack them rather than create
another mostly empty split pane. Let recent entries be pinned/saved, renamed when saved, removed
individually, and history cleared/disabled. Preserve the complete query and supported filters/sort,
not just text. Keep Archive and Media search scopes explicit: their query semantics are not
interchangeable merely because the visual component is shared. Keyboard traversal, accessible
names, duplicate suppression and avoiding history writes for each keystroke are acceptance topics.

Recommendation pending user decision: treat this as planned feature work rather than silently
expanding the currently running 1.1.0 release-validation scope. Existing broken behavior remains
separate from adding this new shared experience.

### UX-06 — Loaded media regresses to a permanent loading placeholder

- **Report:** With the reader sidebar open, Up/Down navigation briefly shows a new image, then a gray loading placeholder replaces it indefinitely.
- **Confirmed old-build cause:** A delayed placeholder timer remained active while neighbour preloads ran after the selected image had already been displayed. Current-source fix `25bacbc72` addresses this; it is absent from the reported old build.
- **Remaining hardening:** Immediately invalidate old requests on selection changes; ensure current-request cancellation ends loading; retain a valid same-medium image when a higher-resolution replacement fails. Dedicated worker/tests in progress, not accepted yet.
- **Cross-view review:** Archive/large reader sidebar and Media sidebar; rapid forward/back navigation, resize, image/video transitions, missing files and disposal.
- **Evidence required:** Deterministically reverse completion order and delay preview/full decode; stale results never alter the current medium or loading state. A previous post's image must not masquerade as the newly selected medium.

### UX-07 — Update action icon consistency

- **Report:** Check for updates should show a refresh icon.
- **Source finding:** Application command already declares `refresh-cw`; About's button still assigns text-only content.
- **Expected:** Consistent icon and localized accessible label, including checking/disabled states; no change to update/network policy. Implemented via shared command presentation in `0c4ffdc91`; integrated localization/presentation contracts pass. Native visual review remains pending.
