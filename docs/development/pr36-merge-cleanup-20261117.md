# PR #36 merge cleanup — 2026-11-17

Merged `origin/goal/gesamtprogramm-profile-a` at `17db174fb` into `ux/ledger-15-overlay-hover-release-20261117` with an ordinary merge commit. There were **no textual conflicts**: Git automatically combined both intents in the overlay handler.

Verified `OnOverlayPointerMovedOrExited` remains the declaration and subscription/reflection name inherited from #53. Its body still returns through `ReleaseTransientOverlays(keepKeyboard: IsGalleryWindowActive && _keyboardInteraction)` and `UpdateViewerInteractionTimer()` when `HasCurrentGalleryPointer` is false, before `RememberPointer`. Current pointer presence, active-window and available-surface gating from #36 were not discarded by the rename.

The stale `ReaderMediaSidebarLayoutTests` expectation is corrected exactly as in #58: all three work-view policies must be `Presence.Direct`, and the toggle must have `OverflowMode.AsNeeded`. No either/or assertion, suppression, skipped test or production overflow change was introduced.

## Dependency and readiness

#58 was made GitHub-mergeable but was not landed while a real analyzer build blocker remained. Therefore this merge includes the current goal, **not** an unlanded feature branch. Once #58 and the target-branch analyzer repairs land, merge goal again before marking this PR ready. The standalone stale-expectation correction here is identical to #58's policy correction and should converge automatically.

The target itself is not green: an untouched `17db174fb` worktree running `dotnet build MeowChan.csproj` failed with **21 analyzer errors**. See #58's `docs/development/evidence/pr58-merge-cleanup-20261117/pristine-goal-analyzer-errors.log`. The four-rule gate was not disabled in project configuration and the frozen baseline was not reseeded.

Only affected application/DesktopTests graphs are in scope. No solution build or GUI smoke suite. A documented warning-only analyzer capture may be used to compile diagnostic test binaries, but passing tests from those binaries must not be represented as passing the normal build gate.

## Final validation

Normal `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --filter '<overlay + sidebar selectors>'` failed during application compilation, before running tests: **34 distinct analyzer errors**. Beyond the 21 already reproduced on goal, this branch exposes **13 additional diagnostics**:

- CA2000 for unretained `TransientHover` leases in GalleryDecisionRow, GalleryFilmstrip, GalleryVideoPlayer.Scrub, ReaderBodyTextBlock, MainWindow.MediaFocusChrome, ArchivePage.Feedback, ArchivePage.Reader (two) and ReaderMediaSidebar.
- CA2000 for the bitmap allocation in the edited `GalleryVideoPlayer.Scrub.cs:326` and CA2213 for `_scrubHoverCancellation` in that file.
- CA1001 for `ReaderHoverEnlarge._hoverLease` and for the new `ArchivePage._quotePointerLease` ownership (the partial-class diagnostic is reported in ArchiveExportLinkListHandoff).

These are real build-gate blockers, not a remaining stale toolbar assertion. No claim is made that every diagnostic is a proven runtime leak; each needs an analyzer-visible, correct lifetime implementation before the gate can pass. No suppressions were added and these lifecycle repairs were not forced into this cleanup.

For diagnostic test execution only, rebuilt **MeowChan.csproj** and **MeowChan.DesktopTests.csproj**, each with:

```sh
--no-restore --no-incremental -p:BuildProjectReferences=false \
-p:DisableAnalyzerBaseline=true -p:WarningsAsErrors= \
-p:AnalyzerBaselineCaptureDirectory="$COMMANDCODE_SCRATCHPAD/pr36-analyzer-capture"
```

Then `dotnet test ... --no-build --no-restore --logger 'console;verbosity=normal'` selected these classes with `FullyQualifiedName~` selectors joined by `|`:

```text
GalleryOverlayActivityTests
TransientHoverTests
QuotePreviewMediaTests
ArchiveMediaBadgesTests
ChipStyleTests
GalleryDecisionRowDesignTests
GalleryFilmstripTests
MediaFocusFullscreenTests
MediaLibraryBadgeTests
ReaderMediaSidebarLayoutTests
ReaderSidebarAlignmentTests
ReaderMediaSidebarTests
GalleryTerminalRetirementTests
```

This covers every test class added or modified by the original overlay PR, both sidebar layout/rule suites, quote cancellation, and the renamed-handler retirement regression.

```text
Test Run Successful.
Total tests: 362
     Passed: 362
 Total time: 55.7825 Seconds
```

**0 failed / 0 skipped**. Tests ran against newly compiled current-merge diagnostic binaries, not old artifacts. This passing component lane does **not** turn the normal analyzer build green. #36 remains draft because of the 34 application analyzer blockers and the unlanded #58 dependency.

Receipts: [`evidence/pr36-merge-cleanup-20261117/`](evidence/pr36-merge-cleanup-20261117/), including complete focused test output and all 34 normal-gate diagnostics.
