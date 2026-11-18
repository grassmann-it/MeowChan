# Gallery/Reader native target readiness — ARM 43908445672

Artifact `14248509835`, `validation-macos-15-native-ARM64`, source `ccb3be5e5`.
Downloaded via direct GitHub API into `/tmp/ochan-arm-full-43908445672/`.
Patch base `0c1839030`. No production player, demux, shutdown or apphost edits.

## First failures in the ARM artifact

- `artifacts/closure-gallery-errors/normal/output.log:78–82`: strict preparation
  fails at `GalleryErrorInteractionChecks.cs:182`, `MoveAsync(footer)`.
  No context-root PASS preceded this: first configuration is external=false,
  postOverlay=false. Target is `GalleryOverlayFooter`, after height 180/280/180.
- `artifacts/closure-reader-media-input/reader-ux/output.log:77–82`: fixture selection
  and exact post-1001 media association are ready; first native body click fails at
  `ReaderInteractionChecks.cs:392`, called by line480. Target `PostBody` requested
  at `(16,10)`. No native-pointer-down was delivered before preparation failed.
- Both fail in `PrepareTargetAsync`, not native submission, popup settling or decoding.
  That artifact did not record individual hit predicates, so the root-hit details
  below are from the matching local reproduction, not invented ARM measurements.

## Local reproduction, checks unchanged (only terminal diagnostics added)

Private Xvfb/Openbox native input; base `0c1839030`:

- `/tmp/mac-native-readiness-gallery-before/normal/output.log`: footer attached,
  arrange-valid, visible, enabled, hit-test-visible; bounds `(0,520,1000,180)`.
  Geometry ready, center NOT ready. Center `(500,90)` hits `GalleryViewport`;
  target-local hit null. The height-forced transparent StackPanel has empty center.
- `/tmp/mac-native-readiness-reader-before/reader-ux/output.log:88`: PostBody attached,
  arrange-valid, visible, enabled, hit-test-visible; bounds `(0,0,429,44)`.
  Geometry ready, center NOT ready. Center `(214.5,22)` hits `PART_ContentPresenter`;
  target-local hit null. SelectableTextBlock's stretched rectangle is not a filled
  pointer surface. Preparation rejected its blank center before resolving `(16,10)`.

## Changes

- Gallery moves to the painted `GalleryOverlayControls` Border, proves it belongs
  to the same height-forced footer, retains strict center readiness, then requires
  BOTH footer and closing-root PointerOver before retirement.
- Reader targets the existing post-1001 ListBoxItem (strict center readiness),
  translates the original `(16,10)` text point into it, and additionally requires
  the MAIN WINDOW hit at that actual screen coordinate to be the exact body or its
  descendant. Original routed pointer-body, focus, selected-post and activation
  assertions remain unchanged. No added Background, synthetic input or Focus call.
- Terminal readiness diagnostics now reach output.log even without a trace callback:
  geometry/center predicates, root/local hits and target identity. No reader text
  or media paths are logged.

No center-check removal, hit-test opt-out, timeout increase, or input retries added.
No CI dispatched; macOS post-patch validation remains for the central runner.

## Validation

- Both original failures reproduced with real X11 input before the target changes
  (diagnostics-only build, unchanged strict readiness predicates).
- Corrected Release DesktopSmoke build completed: 0 errors, 45 warnings.
- Python native-input wrapper contracts: 12 passed (mocked driver, not GUI acceptance).
- Post-patch GUI scenarios were not started: user requested no broad GUI suite while
  the host is overloaded. Their green native acceptance remains pending.
- `git diff --check` clean.
