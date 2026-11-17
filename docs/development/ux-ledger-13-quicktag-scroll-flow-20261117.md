# UX-LEDGER-13 — Quick-tag scroll ownership

## Contract and implementation

Assigned tags must scroll away before suggestions in **one continuous flow**, not in separately fixed scroll zones. Suggestions keep at least **three complete wrapped rows**, when the bounded candidate pool contains enough items; fewer candidates are all retained. Three rows follow the implementation request (the analysis also mentioned a two-row example). Suggestions may initially be below the fold. A physically shorter viewport still scrolls; this is not a promise to display three rows simultaneously on an arbitrarily short screen.

`QuickTagSurface` now owns one outer vertical `ScrollViewer`, shared by the gallery panel and both taggable popover variants. The popover keeps its existing overall height/width bounds and delegates scrolling to that reusable surface; it does not introduce another viewport. All six content rows and all suggestion-area rows are `Auto`. Assigned tags and suggestions keep their wrapping panels and natural content height. The previous assigned-area maximum/share and both independently scrolling inner regions are gone.

The gallery's bounded strip realization uses the **outer viewport**, never the height remaining after assigned tags. Its fit keeps the configured count and at least three complete rows. Popovers keep the configured count or three complete rows, whichever needs more chips. Both hosts request the existing bounded candidate pool (panel suggestion limit, at least the configured count), so low count settings cannot themselves turn available suggestions into a single row. The first nine number shortcuts and all existing key routes remain unchanged.

The nonempty strip also reserves three 36-dip hit-zone rows (28-dip pill plus 4-dip inset above/below), without a maximum or separate scrollbar. Keyboard focus requests bring the target into view through the same outer owner. Existing input, chip, footer and close-button tab ordering is preserved.

## Deterministic evidence

- `QuickTagStripFitTests`: exact counts for three-row floors under zero/one/two-row height pressure, different widths, insufficient candidates and a larger configured minimum; existing fit tests remain.
- `QuickTagScrollFlowTests`: installed Semi scroll template and real Avalonia measure/arrange, 60 assigned tags, 18 suggestions, narrow/wide gallery panel and tag/rate-and-tag popover frames. Checks overflow extent, assigned wrapping, below-fold suggestions, scrolling past assigned tags, at least three fully visible suggestion rows, no inner assigned/suggestion viewport, routed focus reveal and commit/re-layout.
- `QuickTagSurfaceTests`: existing keyboard routing, refresh ordering, accessible names and focus-order contracts retained. Structural expectations were changed from residual star slots to the new auto-sized flow, not relaxed.

## Validation receipt — 17 November 2026

Recovered work was rebased onto fetched `origin/goal/gesamtprogramm-profile-a` at `ae3bf885c` before implementation commit `da6970275`. Upstream counter relocation and pointer/focus changes were retained; `IdentitySuggestionBox.cs`, `QuickTagPopover.cs` and `GallerySidePanel.cs` are unchanged from that base. PR: https://github.com/grassmann-it/MeowChan/pull/42.

Only the affected desktop-test project was selected as the build/test entry point (including its normal desktop/dependency build targets); no solution build or GUI smoke suite was run:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter 'FullyQualifiedName~QuickTagScrollFlowTests' --no-restore \
  -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
# Passed: 6, Failed: 0, Skipped: 0; duration 7 s. Build completed with existing analyzer warnings.

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-build --no-restore --filter 'FullyQualifiedName~QuickTag' \
  --logger 'console;verbosity=minimal'
# Passed: 231, Failed: 0, Skipped: 0; duration 1 m 2 s.
```

The recovered layout fixture initially failed because its unattached root never ran Avalonia 12's `ScrollContentPresenter.AttachToScrollViewer` lifecycle hook. The fixture now invokes that real hookup explicitly, preserving all extent, row-visibility, focus-reveal and mutation assertions. No production workaround or weakened assertion was introduced for that fixture issue.

The layout fixture uses fixed chip dimensions and managed focus events; it is not native keyboard/wheel/touch acceptance. Native popover placement, touchpad/wheel input, screen-reader traversal, themes/contrast and full half/normal/full-window screenshot review remain unverified by this deterministic suite. Unrelated media, toolbar and entry-point checklist classes were not changed.
