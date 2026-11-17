# UX-LEDGER-27 — Media page layout

## A — Selection grouping

Before: `N selected | Queue · Tag together · Selection as gallery · Export · More | Clear selection`.

After: `Queue · Tag together · Selection as gallery · Export · More | reason / slack | Clear selection · N selected`.

Commands lead as one non-wrapping group; clear and the polite live count dock together at the end. The building reason remains after commands in the fill area. The 32 dip control-height resource and counter ReserveText are unchanged. FitSelectionLine measures both groups, retaining its clear-shortening and overflow stages.

Recommendation: **align Tagging to this new actions-left / clear-and-counter-right contract** in a separate change. It currently puts count left, actions middle, clear right. This PR deliberately does not change Tagging.

Validation: affected desktop test project only; selection layout and multi-selection command filters: **16 passed, 0 failed, 0 skipped**. Layout assertions measure 760 / 1100 / 1440 dip widths, selection entry/exit grid position, 32 dip height, left/right grouping, polite count and reserved width. The headless fixture supplies the product control-height resource locally (its bare visual root has no application resource parent). No GUI smoke suite or native-input / theme / screenshot acceptance claimed.

## B — Full-height sidebar (protocol 1.9e)

Cause: there was no explicit MaxHeight. The page put the panel into the body-only row **below** the page header. `AlignPanel` deliberately matched its extent to the grid card with an 8 dip bottom margin, and lifted the title/actions by a negative top margin onto the selection/summary line. This capped the sidebar to its neighbour's layout and made the actions look detached; its own border could not divide the full page height.

The header/filter/selection chrome now lives in the grid column of `MediaContentRow` (`Auto,*` rows). The sidebar and splitter span both rows from the top to the bottom of that host. No vertical margins, negative header lift, summary-line reservation or neighbour-derived insets remain. The full-height side-facing border remains on the panel, with the shared identifying title/actions line above and transport below; media absorbs all remaining height. The toolbar no longer reserves sidebar width a second time. Closing the panel returns its column immediately.

Checked the parallel Reader implementation at `bfb02cd8d` (`ux/ledger-26-media-sidebar-full-height-20261117`). The solution shape is the same: span header/content rows, keep neighbouring chrome in its own column, remove negative lifts and neighbour-derived vertical insets, retain the shared `ReaderMediaSidebar` title/media/transport composition. No Reader or Tagging production code changed here.

`MediaPagePanelLayoutTests` measures the actual page/panel host at 900×600, 1100×780 and 1440×1000, left and right sides, then adds 200 dip height. Assertions cover exact host/panel/splitter height equality, full-height divider, fixed internal title line, media absorbing the entire height increase, non-decreasing media/host ratio, neighbouring filter expansion not capping the panel, and immediate space return on close. Selection regression additionally measures reserved count width at 9/10, 99/100 and 999/1000 boundaries.

### Validation and limits

Only `tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj` and its desktop/project-reference build graph were built/tested. No solution build or GUI smoke suite ran. The existing Media panel smoke geometry assertions were updated from the superseded grid-card/lift contract to full-host height; that smoke was **not executed or separately built**.

Final affected filter (selection layout/commands, page/panel layout, panel keys, reader shared view geometry, sort toolbar):

```text
Passed! - Failed: 0, Passed: 233, Skipped: 0, Total: 233
```

A broader exploratory run including unrelated Reader toolbar tests was **235 passed, 1 failed**. The isolated failure reproduces: `ReaderMediaSidebarLayoutTests.DefaultToolbarStateMatchesTheDefaultScopeWithoutChangingPreferences` expects toolbar overflow `Never` but gets `AsNeeded`. The Reader production files and that test are unchanged from the starting base `1dbd86c84`; its assertion was not weakened or suppressed. The final filter covers the shared media-view geometry, not unrelated Reader toolbar defaults.

An existing MediaPageLayout source assertion still expected the old inline `ResolveMenuOrigin` call, already absent in the starting base. It now asserts both the resolved `origin` assignment and `OpenSharedMediaMenu(row, origin)` call, preserving the original contract rather than removing the check.

Evidence logs in the session scratchpad: `defect-a-tests.log`, `panel-geometry-tests.log`, `final-targeted-tests.log`, `reader-toolbar-existing-failure.log`. Build emits existing analyzer/Avalonia private API warnings. Native pointer/keyboard acceptance, screenshot/theme/contrast review and GUI smokes remain unverified by policy; this is focused measured-layout evidence, not full UX acceptance.
