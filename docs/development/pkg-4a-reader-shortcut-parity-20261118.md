# PKG-4A Reader shortcut parity — 2026-11-18

## Scope

This audit is limited to the Reader-adjacent media panel shortcut path. It does not change
DesktopSmoke, workflow files, analyzer baselines, lockfiles, or build infrastructure.

## Reproducible gaps

1. `MediaPage.TryRoutePanelPageKey` rejected every `Enter` key before checking the
   effective `gallery.open` binding. The default `gallery.open` gesture is `Shift+Enter`,
   so a focused panel picture could not execute the shortcut advertised by the panel's
   **Open in gallery** affordance. The same early guard also made a user rebind of
   `gallery.open` to Enter unreachable. Native controls remain protected: the fix still
   returns before routing when `CommandRouter.IsNativeInput` owns the event.
2. The Archive reader's media lists presented and routed `media.open`, a Media-page-only
   command, even though the reader surface advertises `gallery.open`. That left the
   reader list's shortcut presentation and its effective Reader command out of parity.

## Change

- The panel now checks `gallery.open` after the native-input/context guards and before the
  remaining Enter/Space/Escape/Tab exclusions, then starts the existing
  `StartOpenPanelMedia()` path. This reuses the panel's normal guarded gallery-open flow
  and does not introduce a second activation path.
- Archive reader media lists now bind and route `gallery.open` consistently. The separate
  `media.open` command remains unchanged for the Media page.

## Focused regression coverage

- `MediaPanelPageKeysTests.ShiftEnterOnThePanelPictureUsesTheGalleryOpenShortcut`
  raises the real routed key on the panel frame and asserts one gallery open at the
  displayed medium.
- `MediaPanelPageKeysTests.EnterAndSpaceStayWithTheFocusedPanelButton` remains in the
  focused set to prove native button activation is not stolen.
- `ReaderGalleryShortcutParityTests.ReaderMediaListsUseTheReaderGalleryCommandForBothPresentationAndRouting`
  guards the reader list's command ID at both registration and routed execution.
- Existing `MediaTileEnterTests` remain part of the adjacent media activation check.

## Gate warning

The package gate is intentionally not claimed here. DesktopSmoke/native keyboard acceptance,
Windows/macOS graphical runs, and any analyzer-baseline or build-infrastructure work are
outside this worker's allowed scope. The PR must run the affected desktop test selector on
its merge head; native platform and smoke gates remain pending there.
