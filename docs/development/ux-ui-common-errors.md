# UX/UI common errors — checklist for reviews and AI test runners

The generic, cross-project rules live in the global `ux-ui` skill
(`~/.commandcode/skills/ux-ui/`; references: `design-language.md`, `interaction.md`,
`common-errors.md`, `review-routine.md`, `planning.md` under `references/`).
This file is the oChan/MeowChan instance with project-specific classes and examples.

Persistent list of error classes that keep recurring. Every UX/UI review or GUI sweep (human or
AI runner) checks the whole list, not only the screen that changed. Add a class here when a bug
turns out to be an instance of a pattern; dated backlogs such as
`ux-ui-release-followups-20261117.md` hold the individual findings.

## Review workflow

- Reviewers get artifacts, not descriptions: screenshots at half, normal and full-window size,
  short screen recordings for focus/motion issues, and the smoke/runtime log of the run.
- UX/UI reviews: GPT-6 Astra at max reasoning effort first; Claude Fable 5.2 only as an
  emergency fallback.
- Every finding names its class below (or adds a new one) and the evidence artifact.

## Error classes

### Keyboard and focus

- **Text input swallowed:** a parent/popover marks keys handled, so text boxes never receive
  typed text. Type real letters, digits, Space, Backspace and IME input into every field right
  after it opens.
- **Autocomplete acceptance (Enter or click):** owner tunnel handlers can run before the
  field; pointer focus repair can cancel a captured popup-row click. Accept the chosen full
  suggestion, not the draft prefix or another “New tag” row. Completion replaces only the token
  under the caret and preserves quoted tags with spaces.
  Regression coverage from #28 (`bb4c131ca`):
  [`TagAutocompleteCommitChecks.cs`](../../tests/MeowChan.DesktopSmoke/TagAutocompleteCommitChecks.cs)
  (`tag-autocomplete-commit`) exercises click/highlight-click/Enter, aliases, source/create rows,
  middle/quoted tokens, Tab then Enter, comma behavior and a composition guard. It uses Linux
  XTest clicks, routed keys and simulated preedit, not Windows/macOS or native IME certification.
  #30 (`b849f94fd`) extends
  [`QuickTagSurfaceTests.cs`](../../tests/MeowChan.DesktopTests/QuickTagSurfaceTests.cs)
  to assert editor-visual-only focus repair and no redundant refocus while the input owns focus.
- **Focus/hover contract (focus stranded after pointer clicks):** clicking chrome (transport,
  mute, toggles, toolbar) must return keys to the gallery/reader; keyboard activation keeps native
  focus. A hand cursor must promise a real single-click action; hover alone must not activate.
  Regression references:
  [`FocusClickContractTests.cs`](../../tests/MeowChan.DesktopTests/FocusClickContractTests.cs),
  especially `ReaderSidebarSingleClickMustNotOpenGalleryForAnyReaderPreference` and
  `GalleryViewportRightClickMustFocusSurfaceBeforeOpeningMenu` (enabled by #26 / `f159827dd`),
  plus `MediaRightClickPreservesOnlyASelectionContainingTheTargetAndFocusesBeforeMenu`
  (ordering restored in `cde745dfc`). Reader cursor/action consistency is also asserted in
  [`MediaViewsDeadAffordanceTests.cs`](../../tests/MeowChan.DesktopTests/MediaViewsDeadAffordanceTests.cs).
  These are source/wiring contracts, not native hover/pointer acceptance. Tagging right-click
  (FC-02) and Gallery filmstrip targeting (FC-04) remain skipped/open; #23's selection lifecycle
  coverage does not close them. See the [current closeout](ux-ui-release-followups-20261117.md#focus-click-contract-skip-closeout).
- **Shortcut routing by focus:** rating, favorite, tag, navigation and video keys must reach the
  right surface when a sidebar, thumbnail, post or video has focus, without stealing text keys.
- **Focus not restored on close:** closing a popover, dialog, sidebar or enlarged view returns
  focus to the element that opened it. Esc closes only the topmost layer.
- **Held keys auto-repeat** the opening action into the newly opened surface.

### Selection and pointer semantics

- Single-click selects, double-click opens only where documented, right-click preserves the
  target (and acts on the clicked item, not an unrelated multi-selection).
- No hand cursor without a single-click action.
- Keys must not fail silently with a multi-selection; give feedback or act on all.

### Layout and visual stability

- **Jumps on entry/data refresh:** widths, toolbars and grids must not shift when the selection
  or data changes; check counters at digit-count boundaries (e.g. 999/1000, 1099/1100).
- **Inconsistent use of width:** toolbars, List/Table toggles and search fields align to the
  content column at half, normal and full-window sizes.
- **Media loading flashes:** keep the previous image until the replacement is ready (or show a
  placeholder); never a stale flash or a permanent grey surface; videos must not pause, restart
  or turn grey on focus changes.

### Icons, labels and text

- Every icon-only action has a tooltip and accessible name; close / clear / remove / reset are
  visually distinguishable.
- Menu entries that open dialogs end with an ellipsis; menu icons match their header affordance.
- Status and empty-state texts are truthful and actionable (state the repair step), with one
  consistent name per dialog/action across the app.

### Entry points and state

- Opening from Archive/Downloads/Media selects and shows the first post/medium immediately.
- Enlarging keeps the selected item and never overwrites the reading position.
- A damaged or missing index/cache degrades one feature, never blocks unrelated pages.

### Accessibility

- Contrast of switches, edge handles and focus rings meets WCAG AA in all themes.
- Accessible selection state matches the visible page (settings categories, tabs, lists).
