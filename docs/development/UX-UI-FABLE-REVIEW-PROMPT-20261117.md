# Fable UX/UI release review prompt

Use this prompt with the repository root and the issue ledger
[`ux-ui-release-followups-20261117.md`](ux-ui-release-followups-20261117.md). Review the
current checkout, not a historical branch. The goal is to find complete **error classes**, not
only the one screenshot that exposed them.

## Prompt

You are the final UX/UI and interaction reviewer for MeowChan 1.1.0. Read the UX/UI backlog and
all relevant code, tests, smoke scenes, screenshots, and release notes before judging anything.
Do not silently fix or dismiss a finding. For every finding, classify it as **release blocker**,
**1.1.1 quick win**, **1.2 feature**, or **2.0 redesign**, and record:

- exact page, entry point, control name, and current state;
- mouse path, keyboard path, and expected path;
- whether focus is on a text editor, popup, sidebar/media item, reader, grid/list, or page;
- the smallest reproducible steps and the affected platforms/scales;
- evidence: screenshot path, smoke scene/test, or a precise source location;
- a proposed fix that preserves accessibility, localization, and existing command semantics.

### Review all error classes

1. **Command routing and focus**
   - Verify every shortcut from Downloads, Archive, Media, Reader, sidebars, thumbnails, grids,
     tables, context menus, dialogs, and popups.
   - `R` must open rating/tagging when a media item/sidebar is focused; favorite, tagging,
     navigation, playback, reader, and gallery commands must reach the intended owner.
   - TextBox, ComboBox, numeric input, popup, and IME input must retain priority; shortcuts must
     not steal normal typing, caret movement, or quick-tag entry.
   - Define and test shortcut scope parity: media-sidebar/reader focus must expose the same
     media actions as Gallery through an explicit modifier such as `Alt+<gallery shortcut>`, while
     unmodified keys remain owned by the reader and text-entry controls. Check discoverability and
     localized help text for every modifier route.
   - Test quick tags with number/letter shortcuts and mouse click, including repeated use,
     keyboard-only operation, focus restoration, and screen-reader names.

2. **Menus, context actions, and affordance semantics**
   - Right-click must open exactly the actions appropriate to the selected object and execute the
     action that was clicked.
   - Check every icon-only action for tooltip, accessible name, visual meaning, disabled state,
     and consistency. Distinguish close, clear, remove, reset, overflow, refresh, view, favorite,
     and rating icons; never use an ambiguous generic `X` where a domain icon is expected.

3. **Layout, alignment, and empty space**
   - Inspect full, half, narrow, and resized windows; at minimum 760, 920, 1099, 1100, and
     1440 DIP where the relevant page supports it.
   - Look for unused space, controls that do not fill their row, inconsistent gutters, accidental
     clipping, toolbar wrapping, unstable overflow, and List/Table controls that leave a dead
     strip instead of using available width.
   - Compare page headers, section lines, search fields, filters, split panes, cards, tables,
     footer navigation, and Settings columns against the shared design language.

4. **Entry points and selected state**
   - Opening a thread, archive row, download row, media tile, or context-menu reader action must
     select and show the first expected post/media immediately.
   - A selected item must be visibly marked, focused, and available to enlarge/open in the
     correct pane; never show an empty reader beside a selected row without explanation.
   - Verify favorite/rating/tag state is hydrated before the user can act, and remains consistent
     between thumbnail, post, sidebar, reader, and dialog.

5. **Media loading and navigation**
   - With a sidebar open, Up/Down and reader/gallery commands must navigate posts predictably.
   - During replacement, retain the old image until a new frame is ready or show a deliberate
     low-resolution placeholder; do not flash valid content and then regress to a permanent grey
     loading surface because of a stale completion or race.
   - Test video duration badges, playback, seek, pause, mute, fullscreen, and native input on
     every supported runner where the CI matrix claims evidence.

6. **Settings, themes, and responsive behavior**
   - Search, Reset, Save, Discard, conditional rows, footer ordering, defaults, and advanced
     sections must be discoverable and aligned. Settings search should share the content column.
   - Check dark/light themes, contrast, disabled/focus/hover states, localization expansion,
     screen-reader names, and keyboard traversal. No control may become unreachable when rows
     move under More or when a window is resized.

7. **Booru/plugins and failure recovery**
   - Verify Danbooru search, plugin selection, signature/SDK mismatch diagnostics, repair actions,
     retry, disable/re-enable, and stale pinned builds. Errors must describe the actionable next
     step and must not silently change user selections.

8. **Release communication and validation**
   - Compare 1.1.0 behavior and UI with 1.0.2. Record notable features for release notes and
     README/screenshots, and flag missing help text, shortcut documentation, contribution/help
     links, translations, and support/credits wording.
   - Check that tests continue after independent failures, that each platform's evidence is real,
     and that skipped/quarantined/native lanes are not presented as green proof.

For each class, inspect at least one implementation path and one rendered path. Use screenshots
for visually verifiable findings and add or extend a deterministic smoke/contract test for every
release-blocking interaction. Separate a genuine product bug from a stale smoke expectation,
platform limitation, and infrastructure failure. Finish with:

- a prioritized table of findings;
- a “fixed and rechecked” list with commit/test/screenshot evidence;
- a “not fixed” list with target release and rationale;
- a features-since-1.0.2 summary;
- the exact validation commands and platform coverage.

Do not claim release readiness when a required platform lane, native smoke, upgrade fixture, or
critical keyboard/mouse path is unverified.
