# UX-LEDGER-30 — gallery/tagging centering and sidebar insets

Date: 2026-11-18  
Base: `origin/goal/gesamtprogramm-profile-a` at `e33d98f4a`  
Release target: MeowChan 1.1.0  
Error classes: UX-E11, UX-E12, UX-E13, UX-E17, UX-E27, UX-E28.

## Design decision

- The gallery toolbar now has three semantic regions owned by `GeometricCenterPanel`: mode at the leading edge, previous/position/next as the sole geometric centre, and zoom/media/work-view/layout actions at the trailing edge. The centre is computed against the whole toolbar, not the unequal remaining gap; under impossible pressure it yields to the real edge gap instead of clipping or wrapping.
- Tagging queue decisions/status keep a separate row at every supported width. This leaves the gallery toolbar's geometry identical in View and Tag mode. Decision actions lead; the passive open/done/skipped progress is trailing.
- The detached `Medium n of m` badge uses equal star regions around an auto-sized centre column. Its label is centred too. If the measured segment/counter/close widths cannot coexist without overlap, the counter owns a centred second header row; this is content-driven rather than a magic breakpoint. Existing two-line localization, live-region, tooltip and focus contracts remain.
- Sidebar spacing uses `Workspace.Inset.Section`. `WorkspaceMetrics.SectionInset` is the tested code mirror for hosts that must override individual edges. Scroll-owned gallery/tag content receives the same inset inside the viewport, leaving the vertical scrollbar in its own outer gutter rather than treating the thumb/gutter as right content padding.

No spacer control or one-off corrective margin was added.

## Measured regressions

`GalleryMeasuredGeometryTests` measures the production controls:

- toolbar centre at 760 / 1100 / 1440 DIP in Light and Dark;
- counter centre at 280 / 320 / 480 DIP;
- equal left/right 16-DIP section insets for gallery post scrolling and the shared Reader/Media sidebar.

The native-window `gallery-toolbar` selector additionally measures `paginationCenterDelta`; all 32 width × theme × language × panel-state cells report zero and remain one row. The counter smoke reports `centerDelta=0.500` DIP for all English/German 280/320/480 cells (pixel rounding).

## Validation receipts

- Focused DesktopTests: **544 passed, 0 failed, 0 skipped**.
- Release build: **passed**, 0 errors; existing warnings only.
- `gallery-toolbar` registry selector: **passed**. Matrix: 760, 1099, 1100, 1440; Light/Dark; English/German; panel closed/open. Receipts and logs: `artifacts/ux-1.1.0-ui/smoke-gallery-toolbar-v2/`; screenshots: `artifacts/ux-1.1.0-ui/gallery-toolbar/`.
- `tagging-mode` registry selector: **passed** after repairing stale smoke expectations to the registered `Primary+Enter` and `S` bindings. Receipts/screenshots: `artifacts/ux-1.1.0-ui/smoke-tagging-mode-v5/`.
- `gallery-media-tags`: all six requested counter geometry/localization cells and their screenshots passed, including the measured narrow-header second row without overlap; then the selector failed in its later independent archive-window disposal journey (`DisposeWindowAsync` shutdown ownership). Target evidence is retained under `artifacts/ux-1.1.0-ui/smoke-gallery-media-tags-v4/`; aggregate selector remains failed/incomplete.
- `reader-media-sidebar`: full-height/padding geometry passed and emitted four left/right embedded/large screenshots. The aggregate reader-ux selector remains failed because the independent native Enter shortcut-parity scenes failed. Evidence: `artifacts/ux-1.1.0-ui/smoke-reader-sidebar-v2/`.
- First `gallery-toolbar` attempt is retained as infrastructure evidence: product geometry passed its first 760 Light/English cell, then capture failed because the requested screenshot directory did not exist. The corrected-directory run passed; no product assertion was retried away.

## Coverage limits

Linux/X11 software rendering at scale 1 was exercised. Toolbar screenshots cover Light/Dark and English/German at the requested widths. Counter screenshots cover English/German in Dark at 280/320/480 panel widths; its Light geometry is covered by the measured component test, not a second native screenshot set. Reader screenshots use the reader fixture's theme. Windows/macOS, 125% scale, native IME and independent Astra/Fable review are unverified. Overall full-product acceptance is therefore **incomplete**, despite the focused implementation and toolbar/tagging selectors being green.
