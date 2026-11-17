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

Implementation and validation results will be recorded after the focused fixes.
