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
* `MainWindow.GalleryScrub`: `ApplyLatestAsync` finally disposes the request source;
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

Group 2 validation: targeted Favorites/LatestWins DesktopTests: **28 passed, 0 failed**.

## Group 3: terminal owners

* `ArchiveTransferExportDialog`: implements idempotent IDisposable; Closed/Terminate and the page's
  finally call disposal. Cancel and dispose the dialog lifetime (only child tokens, not that source,
  are handed to workers). Each count/export owns a using scope spanning the entire awaited operation,
  including recounts superseded while waiting for prior I/O. Identity-clear `_operation` before disposal.
  Disposing a window cancels but does not prematurely dispose a non-cooperative worker's child source.
* `GalleryTagPanelSuggestions`: cancel on replacement/Dispose, serialize cancellation with final
  disposal, and dispose **each** worker's source in its completion finally (including errors and
  superseded warm-ups). Do not dispose it while `_build` still runs. The gallery already disposes
  the suggestion adapter when releasing its panel; that caller remains in place.
* `GalleryDecodeQueue`: its existing per-worker using is correct. Add idempotent terminal Dispose
  to cancel/close admission, called from `GalleryViewer.StopAsync`. Active work retains its source
  until the pump exits; `Completion` exposes the actual drain for deterministic tests.
* `UpdateService`: dispose the owned semaphore, NOT the explicitly caller-owned HttpClient;
  reject operations after disposal. `UpdateSession` already drains its operation gate on shutdown;
  it now disposes the service there, and also releases its own semaphore and lifetime CTS.
* `ArchivePage.MediaCounts`: keep the existing terminal `EndLifetime` owner rather than inventing
  another page lifetime. Cancel and await `ArchiveMediaCountTask` before disposing the count gate,
  so even non-cooperative filesystem work can still Release it. No subscription edits.

### Additional, unbounded leak proof before the export fix

With the dialog source still unchanged, run the new headless lifecycle tests against the
already-built pre-group-3 desktop assembly (no native windows, no GUI smoke suite):

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore \
  -p:BuildProjectReferences=false -p:RunAnalyzers=false \
  --filter FullyQualifiedName~CtsOwnerDisposalTests --logger 'console;verbosity=normal'
```

```text
Failed ...CtsOwnerDisposalTests.ClosingExportDoesNotDisposeTheTokenOfANonCooperativeCount
 Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.ObjectDisposedException)
Failed ...CtsOwnerDisposalTests.FiftyClosedExportDialogsReturnParentRegistrationsToBaseline
 Assert.Equal() Failure: Values differ
Expected: 0
Actual:   50
Failed ...CtsOwnerDisposalTests.FiftyCompletedExportRecountsReturnRegistrationsToBaseline
 Assert.Equal() Failure: Values differ
Expected: 0
Actual:   51
Test Run Failed.
Total tests: 3
     Failed: 3
```

The recount test includes the constructor's first count plus 50 recounts. The closed-dialog test
counts registrations on an external parent across 50 separate dialog lifetimes. Both assert zero,
not a looser threshold. Headless platform binding is scoped to each test; Avalonia.Headless matches
the application's Avalonia version. The first attempted owner run lacked a window platform and was
an infrastructure failure, not leak evidence; only the corrected assertion failures above count.

Additional coverage checks 50 exports, queued/superseded early-return recounts, 50 tag warm-ups,
non-cooperative warm-up/decode completion, archive-gate drain ordering, and update transport ownership.

### After: unchanged regression assertions plus completion-ownership coverage

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore \
  --filter FullyQualifiedName~MeowChan.DesktopTests.Cts --logger 'console;verbosity=normal'
```

```text
Passed ...CtsDisposalTests.FiftyCompletedSidebarStripRefreshesReturnParentRegistrationsToBaseline
Passed ...CtsOwnerDisposalTests.ClosingExportDoesNotDisposeTheTokenOfANonCooperativeCount
Passed ...CtsOwnerDisposalTests.FiftyClosedExportDialogsReturnParentRegistrationsToBaseline
Passed ...CtsOwnerDisposalTests.FiftyCompletedExportsReleaseEachOperation
Passed ...CtsOwnerDisposalTests.SupersededCountsWaitingForNonCooperativeIoDisposeOnTheirEarlyReturn
Passed ...CtsOwnerDisposalTests.FiftyCompletedExportRecountsReturnRegistrationsToBaseline
Passed ...CtsResourceOwnerTests.FiftyCompletedTagWarmupsDisposeEverySource
Test Run Successful.
Total tests: 16
     Passed: 16
```

All zero-registration assertions are unchanged. The remaining passing cases cover helper
success/fault/cancellation, warm-up cancellation/fault, decode retirement, delayed gate release,
and update resource/transport ownership. The normal build compiled the affected desktop app and
DesktopTests with analyzers enabled; existing Avalonia/nullable/analyzer warnings remain.
No solution build and no GUI smoke suite. Full raw logs are session scratch artifacts
(`cts-before.log`, `cts-owners-before.log`, `cts-after.log`).

An exploratory filter using bare `~Cts` also matched unrelated method names such as `Selects` and
`Rejects`; that oversized run was stopped and replaced with namespace-qualified affected-class
filters. No assertions were removed or relaxed.

## Final validation

Only the affected desktop application, `tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj`,
and their necessary build dependencies were built. No solution build; no GUI smoke suite.

| Targeted run | Passed | Failed |
| --- | ---: | ---: |
| Group 1 sidebar/panel/CTS | 94 | 0 |
| Group 2 Favorites/LatestWins | 28 | 0 |
| Focused final CTS regressions | 16 | 0 |
| Final CTS/update/tag-context/transfer-export owner classes | 179 | 0 |
| Decode scheduling/LatestUiWork/preview reservation | 36 | 0 |

These are overlapping test runs, not a sum of unique tests. The final owner filter was:

```sh
filter='FullyQualifiedName~MeowChan.DesktopTests.Cts'
for class in UpdateServiceTests UpdateSessionTests UpdateSimulationTests TagContextSuggestionTests \
  GalleryDecodeQueueTests ArchiveMediaCount ArchiveTransferExport; do
  filter="$filter|FullyQualifiedName~MeowChan.DesktopTests.$class"
done
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore \
  --filter "$filter" --logger 'console;verbosity=normal'
```

```text
Test Run Successful.
Total tests: 179
     Passed: 179
```

`git diff --check` passes. The desktop diff adds/removes **no** `LayoutUpdated` subscription lines.
