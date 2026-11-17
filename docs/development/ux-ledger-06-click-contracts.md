# UX-LEDGER-06 — focus/click contract audit

The task's contracts supersede the older Fit/1:1 double-click description in
`ux-ui-release-followups-20261117.md`. Scope: item bodies, not nested buttons,
tag chips, text editors, column headers or empty space. Focus means the clicked
item owns keyboard input before the context menu takes focus, and on dismissal.
Existing multi-selection is preserved only if it contains the right-click target.

## Initial code audit

| Page/surface | Single click | Double click | Right click / focus |
| --- | --- | --- | --- |
| Downloads threads/boards | Native row selection | Thread reader / board details | Selects target; incorrectly focuses table rather than row |
| Archive threads (list/table) | Select + embedded preview | Open reader | Selects target; incorrectly focuses list rather than row |
| Archive media (tiles/table) | Select + preview | Open gallery | Audited with shared media selection/menu handlers |
| Media grid | Native tile selection + panel preview | Open gallery at clicked tile | **Violation:** menu composed for target but selection remains elsewhere |
| Media sidebar picture | Preview/play-pause | Open gallery | **Violation:** image single-click can open gallery when configured; menu can use unrelated multi-selection |
| Reader sidebar picture/strip | Preview/play-pause / select thumbnail | Open gallery | **Violation:** image single-click can open gallery when configured; Reader activation is forbidden here (test only) |
| Gallery viewport (image/video) | Focus/pan; video playback remains native | Fullscreen already implemented in `OnViewDoubleTapped` | Context implementation is out of scope; test only if a violation is found |
| Tagging list/grid | Native selection | Continue here | Context host composes from existing selection, without page-owned target resolution; selection fix is forbidden (test only) |

## Validation scope

Source interaction contracts follow the existing DesktopTests convention for
native GUI controls; they are wiring guards, not OS mouse evidence. Targeted
managed-pointer smokes, where run, are reported separately. No broad smoke
baseline or all-platform acceptance is implied.

## Delivered contract matrix

“Existing” below is a code/wiring audit, not new native pointer evidence. The short
`focus-click-contracts` smoke exercises the actual Downloads view handler on
native table cells with inert row identities, plus the Media grid/sidebar with
managed pointer events; source contracts cover all five requested pages.

| Page / click | Expected | Actual before → after | Disposition |
| --- | --- | --- | --- |
| Downloads / single | Select and focus row; no open | Native row selection | Existing; guarded |
| Downloads / double | Threads: reader; boards: details | Correct hit-row activation | Existing; guarded |
| Downloads / right | Select target unless already selected; menu; target focus | Correct selection, table focus → clicked cell/item focus before menu | Fixed locally |
| Archive threads / single | Select + preview only | Embedded preview, no gallery/large reader | Existing; guarded |
| Archive threads / double | Open reader at hit thread | Correct list/table reader path | Existing; guarded |
| Archive threads / right | Target selection, menu, target focus | Correct selection, list focus → clicked cell/item focus before menu | Fixed locally |
| Archive media / single | Select/preview only | Native selection | Existing; related tests |
| Archive media / double | Open gallery at hit medium | Correct target resolution | Existing; guarded |
| Archive media / right | Preserve selected group only when target is in it | Platform-dependent selection → explicit target selection + row focus before menu facts | Fixed locally |
| Media grid / single | Select/preview only + focus | Native selection; no gallery activation | Existing; guarded + short smoke |
| Media grid / double | Open gallery at hit tile | Correct hit-tile gallery path | Existing; guarded + short smoke |
| Media grid / right | Select hit tile outside group; preserve group inside; menu + focus | Unrelated selection retained → target selected/focused before menu composition | Fixed locally + short smoke |
| Media sidebar / single | Stay in preview, never open gallery | Gallery preference opened gallery → preview/play-pause only + frame focus | Fixed locally + short smoke |
| Media sidebar / double | Explicit gallery activation | `GalleryRequested` still opens gallery at previewed item | Existing; guarded + short smoke |
| Media sidebar / right | Target selection, menu + focus | Could use unrelated group → target selected and preview frame focused | Fixed locally; guarded |
| Reader sidebar / single | Stay in preview for every preference | Still opens gallery with Gallery preference | **Skipped: Reader activation forbidden** |
| Reader sidebar / double | Explicit gallery activation | Existing frame/strip gallery activation preserved | Existing; related tests |
| Reader sidebar / right | Target-resolved menu + item focus | Existing sidebar/strip target-resolution route retained; native reader focus not revalidated | Routing audit only; no Reader activation changes |
| Gallery viewport grid / single | Focus/pan preview; no new gallery | Existing surface focus/pan | Existing; guarded |
| Gallery viewport grid / double | Window fullscreen, not Fit/1:1 | Already raises `FullscreenRequested` for image/native video overlay | Existing; guarded |
| Gallery viewport / right | Menu on shown item; focus belongs to shown item | Correct menu target; no surface-focus reclaim in context handler | **Skipped: Gallery context core forbidden** |
| Gallery filmstrip / right | Select clicked medium, then menu | Menu targets clicked entry without selecting/navigating it | **Skipped: Gallery context core forbidden** |
| Tagging list/grid / single | Select only + native item focus | Native list selection | Existing; guarded |
| Tagging list/grid / double | Continue here | Existing list/grid continue action | Existing; guarded |
| Tagging list/grid / right | Select cursor target outside group; menu + focus | No page-local target resolution before selection-based menu composition; native end-to-end selection not asserted | **Skipped: Tagging selection fix forbidden (one theory case per view)** |

### Protected gaps

`FocusClickContractTests` contains explicit, reasoned skips for Reader single-click,
Tagging list/grid right-click, Gallery viewport right-click focus, and Gallery
filmstrip right-click selection. No Gallery context code, Tagging selection logic,
Reader activation, plugin signature or toolbar/layout XAML was modified. The
Tagging skips are wiring-gap tests, not proof that every native backend fails.

### Validation

- Application build (`MeowChan.csproj`): **passed**, 0 errors; 7 existing warnings.
- DesktopSmoke build: **passed**, 0 errors; existing warnings only.
- New `FocusClickContractTests`: **15 passed, 4 skipped, 0 failed**. xUnit reports
  the skipped Tagging list/grid theory as one skipped test.
- New + related DesktopTests: **398 passed, 4 skipped, 0 failed** (402 total).
  Related coverage includes InteractionAudit, Downloads follow-ups/read reachability,
  Archive lead/preview/media selection, Media panel/layout/tile Enter/menu guards,
  Reader click/sidebar, Tagging selection, Gallery entry points/panning,
  shell routing, list-item Enter parity, and smoke scenario/selector contracts.
- Python smoke-runner unit tests: **42 passed** (`test_desktop_smoke_runner.py`).
- Bounded builds used `DOTNET_PROCESSOR_COUNT=2`, `-m:1`, `--no-restore`,
  `BuildProjectReferences=false`, `UseSharedCompilation=false`, and
  `RunAnalyzers=false` after dependencies had built. The initial unconstrained
  build/test attempt was stopped under host memory pressure, not reported as a pass.
- `focus-click-contracts`: **passed**, no binding errors. The actual Downloads
  pointer handler selects a synthetic table row and focuses its native cell
  (`selected=2`, `rowFocus=True`, `focused=DataGridCell`); inside-selection clicks
  retain both rows. Media grid single/double/right-click, focus after menu close,
  and sidebar single/double-click with Gallery reader preference all passed.
- Related `media-menu-surfaces`: **passed**, no binding errors. Covers Media and
  Archive table/tiles/inline menus, keyboard parity, multi-selection preservation,
  and frozen-menu retirement. Its Gallery menu-target checks do not supersede
  the explicitly skipped Gallery selection/focus contracts above.
- After the final cell-focus opt-in, the targeted DesktopTests suite was rerun:
  **398 passed, 4 skipped, 0 failed** (402 total).
- The first focused probe exposed that both `DataGridRow` and `DataGridCell`
  default to non-focusable. The fix opts only the clicked cell into focus inside
  the local handler; it does not change a shared style or XAML. Failed probe
  attempts were corrected and are not counted as passing runs.
- GUI runs use managed pointer input on private Linux Xvfb/Openbox. A TCP-only
  X server (no Unix socket/lock files) and an abstract DBus socket keep regular
  temporary files inside this long-path worktree. No native OS input, Windows,
  macOS, video-backend or all-platform acceptance is claimed.
- Local logs are under `artifacts/ux-ledger-06/` (ignored, not committed).
  No broad smoke baseline was run.
