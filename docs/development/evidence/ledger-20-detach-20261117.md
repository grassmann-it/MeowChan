# UX-LEDGER-20: current-base transfer audit

Base: `17db174fb` (origin/goal/gesamtprogramm-profile-a, including PRs #58 and #59).

The premise that all four sites are unfixed does not hold on this base. No second transfer mechanism was added.

| Site | Verdict on current base | Evidence |
|---|---|---|
| Gallery-control/context transfer | Defect absent (already protected) | `Desktop/GalleryViewer.Overlay.cs:438` calls `MoveOverlayControl`; lines 396–412 route through `GalleryControlTransfer.Move`. The old line 437 is now a background-update conditional, not a reparent. |
| Normal reader-sidebar transfers | Defect absent (same presentation) | `ArchivePage.MediaSidebar.cs:293–344` moves between `ArchiveReaderPane` and `ReaderLargeFrame`. Both are hosted by the same shell window. No second layout manager/presentation owns the destination. The frame is temporarily detached during construction, not a native window. |
| CloseLargeReader / AttachReader | Defect absent (same presentation) | `Desktop/Shell/MainWindow.Reader.cs:82–90` clears the shell ContentControl then calls `ArchivePage.Reader.cs:450–475`. The pane returns to the same window's `ArchiveSplit`. `ReaderHost` is declared in `MainWindow.axaml`; no separate reader Window is created. |
| Gallery chrome placement | Defect absent (already protected) | `GalleryViewer.Overlay.cs` routes chrome/footer/info/filmstrip/context moves through `MoveOverlayControl[At]` and the same `GalleryControlTransfer`. The hidden footer stays parented explicitly to preserve queued source-root ownership. |

## Original mechanism

A case-insensitive log-message search for `UX-LEDGER-20` returned no commit on this repository's refs. Following the code history identifies `942b3d026` (`fix(gallery): drain old presentation layout before chrome transfers`) as the original fix, subsequently extended for filmstrip insertion by `f98ff1aca`.

`GalleryControlTransfer.Execute` removes the subtree at line 164, drains remembered source roots with real `UpdateLayout` at line 168, and attaches only afterward (lines 179–185). It serializes transactions, retains old root history, permits retarget/cancel during drain, and explicitly takes a no-drain fast path for a **same-root** move (lines 120–123). Applying cross-presentation drains to ordinary reader moves would contradict that existing rule and add reentrant layout during synchronous reader hosting/rollback.

PR #58 changed sidebar sizing/insets, not the single-shell ownership topology. PR #59 changed Media-page row spanning, not Archive reader or gallery hosting. These changes are retained unchanged.

## Deterministic regression coverage

`LargeReaderHostingSafetyTests.NormalSidebarAndReaderTransfersStayInTheShellPresentation` exercises three complete open/close cycles with an existing sidebar and invalidated pane/sidebar measure, checks the shared shell logical ancestor before/after transfer, checks sidebar/splitter parent identity and unique ownership, and pins the production shell host declaration. This is ownership evidence, not a claim to simulate native cross-presentation rendering.

`GalleryPlacementAlreadyUsesTheOriginalDetachDrainTransaction` pins both context/chrome routing to the existing transaction and detach → actual source-root layout drain → attachment ordering. Existing `GalleryControlTransferChecks` additionally exercise real native cross-root drains; they were not rerun as a full smoke suite.

Validation: the affected DesktopTests project was built and the complete `LargeReaderHostingSafetyTests` class passed (65 cases, including the two new audit regressions). The combined run with the nine Media counter theories passed **74/74**, zero skips, in 30 seconds. No solution build or full smoke suite ran.

The initial default project build is blocked by pre-existing `CA1001` at `Desktop/GalleryVideoPlayer.cs:1754` (`VideoCleanupWatch` owns `_timer` but is not IDisposable). Validation was therefore repeated with the command-line-only `-p:RunAnalyzers=false`; assertions were unchanged and no analyzer configuration/baseline or production code was altered. This is not a claim that the default analyzer-enabled build is green.
