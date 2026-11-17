# UX-LEDGER-23: CTS ownership audit

Base: `e2ee324ef` (`origin/goal/gesamtprogramm-profile-a` when this worktree was created).
Only cancellation/disposable ownership is in scope; no LayoutUpdated subscription changes.

## Important base verification

Several reported leaks were already fixed on this base. In particular,
`RefreshMediaSidebar` already calls `CancelMediaSidebarLoad` before replacing the field,
and `UpdateSidebarStrip` already calls `CancelSidebarStripLoad`. The claimed one-orphan-per-refresh
sidebar leak therefore does **not** reproduce on this base. Completed work still retains its final
parent registration until the next refresh/hide, however. The regression below detects that real
remaining retention (one live callback after 50 completed refreshes, not 50).

## Deterministic regression, before the fix

Command:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter FullyQualifiedName~CtsDisposalTests --logger 'console;verbosity=normal'
```

`CtsDisposalTests.FiftyCompletedSidebarStripRefreshesReturnParentRegistrationsToBaseline`
invokes the **real** archive sidebar strip refresh 50 times, checks all 100 thumbnail requests
completed successfully, and counts the live callback list on the page's actual parent CTS.
It does not count GC objects, sleep, mock away production code, or depend on memory pressure.
The reflection probe is explicitly tied to .NET 10 and fails loudly on a changed representation.

Output against unchanged production code:

```text
Failed MeowChan.DesktopTests.CtsDisposalTests.FiftyCompletedSidebarStripRefreshesReturnParentRegistrationsToBaseline
Error Message:
 Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
Test Run Failed.
Total tests: 1
     Failed: 1
```

## Group 1: field ownership

* Archive media sidebar main load, up-to-date neighbour rewarm, thumbnail strip: retire/cancel the
  previous field before replacement, but transfer disposal to the actual task's completion.
  Clear fields by identity so an old completion cannot clear a newer operation.
* Media page panel decode: same task-completion ownership.
* Preview reservation header pass: same task-completion ownership. The existing per-row preview
  prefetch already disposes its CTS in `PrefetchPreviewAsync`'s finally and is unchanged.
* The small `CancellationWork` helper uses `using` around the **awaited work**, not around its
  synchronous launcher. Its tests hold non-cooperative work pending through cancellation and
  verify the token's wait handle is still usable; success, fault, and cancellation all dispose.

## Already paired on this base (not blindly changed)

* `ArchivePage.ReaderView`: previous source disposed on replacement; completion/termination
  disposes through `FinishProjectionBuild`.
* `ArchivePage.Reader` and `ArchivePage.MediaActivation`: shared `ActivateReaderMediaAsync`
  disposes in finally after the manifest/gallery activation completes.
* `ArchivePage.PreviewReservation` per-row prefetch: `PrefetchPreviewAsync` finally.
* `ArchivePage.UrlTextExport`: `RunUrlTextExportAsync` finally, after dialog termination drains writes.
* `MainWindow.GalleryScrub`: `OpenCoreAsync`/completion path disposes the request source;
  terminal drain disposes the lifetime.
* `MediaPage` building and `MediaPage.BooruMode` counting: existing finally blocks dispose each
  source and identity-clear the field. They must not use a `using` in their synchronous launchers.
* `GalleryDecodeQueue`: the active CTS already has a `using` spanning debounce and the complete
  worker operation. Terminal ownership is separate from per-operation ownership.
* `UpdateService`: the injected HttpClient is explicitly **caller-owned**, not service-owned;
  `UpdateSession` owns and disposes the production client. Its timeout and linked CTS already use using.

## Same regression after group 1

The assertion is unchanged: after all 50 real refreshes, registrations equal the original baseline (0).

```text
Passed MeowChan.DesktopTests.CtsDisposalTests.CancelledNonCooperativeWorkOwnsSourceUntilCompletion(outcome: "success")
Passed MeowChan.DesktopTests.CtsDisposalTests.CancelledNonCooperativeWorkOwnsSourceUntilCompletion(outcome: "fault")
Passed MeowChan.DesktopTests.CtsDisposalTests.CancelledNonCooperativeWorkOwnsSourceUntilCompletion(outcome: "cancel")
Passed MeowChan.DesktopTests.CtsDisposalTests.FiftyCompletedSidebarStripRefreshesReturnParentRegistrationsToBaseline
```

Group 1 validation: targeted DesktopTests filter `CtsDisposalTests|ReaderMediaSidebarImageLoadTests|MediaPanel`:
**94 passed, 0 failed**.

## Group 2: genuinely scoped sources

Both Favorites sources already had manual Dispose calls on the successful path on this base.
They now use `using` so exceptions also release the parent registrations.

* Favorites overview (`loading`): window closure alone is NOT the end of the source's lifetime.
  Track and await outstanding scope loads and Export/Remove/View async handlers before leaving
  its using scope. Cancellation closes admission to new work; a non-cooperative resolver can finish safely.
* Original export (`run`): the dialog's Closing handler already refuses closure while the copy is
  running, cancels it, and only permits a subsequent Close after completion. Its source is genuinely
  scoped to the awaited modal method; use `using` rather than a trailing success-only Dispose.
* Other reported locals are actually task-owned and already disposed in finally (see audit above),
  so no unsafe using was added to their synchronous launchers.

Further group results and validation are recorded below as they complete.
