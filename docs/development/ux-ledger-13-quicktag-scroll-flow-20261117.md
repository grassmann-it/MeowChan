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

The layout fixture uses fixed chip dimensions and managed focus events; it is not native keyboard/wheel/touch acceptance. Native popover placement, touchpad/wheel input, screen-reader traversal, themes/contrast and full half/normal/full-window screenshot review remain unverified by this deterministic suite. Unrelated media, toolbar and entry-point checklist classes were not changed.
