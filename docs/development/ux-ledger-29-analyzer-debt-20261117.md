# UX-LEDGER-29 — analyzer debt clearance (2026-11-17)

Base: `17db174fb` (`origin/goal/gesamtprogramm-profile-a` fetched for this branch).

## Outcome

All newly unbaselined diagnostics in the affected desktop/test graph are fixed, with no new suppressions,
configuration relaxations, or baseline additions. The baseline shrank **1,089 → 1,058 (31 removed)**.
The final pruning rerun succeeds with `1058 -> 1058 (0 removed; no additions)`.

### Inventory correction: 42, not 41

A fresh, non-incremental capture produces **42** unbaselined diagnostics: **21 MeowChan + 21 DesktopTests**
(CA2000 34, CA2213 4, CA1001 3, CA2016 1). The pruning command originally refused with **42**.
This report preserves every captured item rather than dropping one to fit the briefing's 41.
The additional subtlety is row 5: `GalleryViewer.Cards.cs` itself is unchanged, but the CA1001 message
now includes `_decodeQueue`. Removing exactly `'_decodeQueue', ` from that message yields SHA256
`79c84806b5b2641fc883df6fc46e70723637f166162712868304577a9b100083`, the OLD frozen message hash.
Thus this is a changed-message/ownership finding, not a changed-file hash or a broken ordinary build.
The other 41 rows are listed as well. The original ordinary-build observation (0 errors, 13 warnings)
is not contradicted: the reproduction below deliberately uses the analyzer-capture configuration.

## Reproduction and final commands

```sh
# Start with an empty capture directory. Project references are included.
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-incremental \
  -m:1 -p:UseSharedCompilation=false \
  -p:DisableAnalyzerBaseline=true -p:WarningsAsErrors= \
  -p:AnalyzerBaselineCaptureDirectory="$COMMANDCODE_SCRATCHPAD/analyzer-capture"
python3 scripts/analyzer-baseline.py "$COMMANDCODE_SCRATCHPAD/analyzer-capture"
```

The initial capture succeeded with 2,292 warnings and 0 compilation errors; pruning refused the 42
unbaselined diagnostics below. The final affected-graph capture contains **zero unbaselined diagnostics**,
not zero total historical diagnostics: 1,058 frozen entries remain across captured/uncaptured assemblies.
During implementation only changed project SARIF files were refreshed after the clean full affected-graph
capture; stale pre-task files were removed, and original capture evidence retained separately.

Final enforced builds (no warning/error-policy overrides):

```sh
dotnet build MeowChan.csproj --no-restore -m:1 \
  -p:BuildProjectReferences=false -p:UseSharedCompilation=false
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -m:1 \
  -p:BuildProjectReferences=false -p:UseSharedCompilation=false
```

Results: application **274 warnings / 0 errors**; DesktopTests **1,340 warnings / 0 errors**.
An earlier normal build of the whole affected DesktopTests project-reference graph also passed
with **1,808 warnings / 0 errors**. These non-incremental analyzer-enabled warning counts are not
claims about the briefing's ordinary incremental 13-warning build.
No solution build, full test suite, GUI smoke suite, or native acceptance run was performed.

## Ownership and implementation

- **Cancellation is not completion.** Decode, tag warm-up, and export already disposed sources in their
  workers. The CA2213 findings were misleading aliases, not proof of an additional runtime leak.
  Owners now retain only cancellation delegates; each worker visibly owns its `using` source.
  Identity checks prevent an older completion clearing newer cancellation authority. Tests retain
  non-cooperative workers and assert token/WaitHandle usability while pending, then disposal after exit.
- **Leases have explicit owners.** Archive/root/sidebar, Media and Rules key-context installations are
  retained through their page lifetime and disposed at terminal teardown. Permanent command registrations
  use `CommandRouter.RegisterOwned`; the router's existing Dispose clears them, without allocating and
  abandoning a separate detachable lease.
- **Video late callers keep their serialization.** `ReleaseEngineAsync` explicitly required the old
  semaphore to survive native cleanup for late stale commands. Disposing it there would be wrong.
  `VideoOperationGate` instead uses managed tasks/queue, has no disposable wait handle, preserves FIFO
  serialization, and never lets a cancelled waiter release an active native call.
- **Disposable contracts propagate honestly.** GalleryViewer, RulesPage, BooruResultViewerWindow,
  BooruWorkbenchView, and MainWindow expose async disposal where appropriate. Existing terminal stop/close
  paths remain the authority; MainWindow refuses to report successful disposal after a veto, an in-progress
  shutdown, or retained ownership. Its disposal task is memoized and its standard protected disposal core
  releases owned fields only after the native/profile shutdown barrier. Pending Booru admission work is
  tracked and awaited before disposing its CTS. No injected session/search model is newly disposed.
- **Native poster cleanup stays off-dispatcher.** A memoized async cleanup with ForceYielding replaces
  Task.Run indirection, preserving retained native roots on failure and making ownership visible. The
  diagnostic timer is retired after cleanup settles, never used to claim native release.
- **Update transport ownership is unchanged.** UpdateService owns its semaphore, NOT its injected client.
  The production client owns its created handler; failed construction releases the handler. UpdateSession's
  documented owned transport is unchanged. Test fixtures now dispose the services they create.
- Scoped native stream wrappers, quick-tag sessions, test queues/CTSs/walks, and exception-safe smoke
  descriptor transfers close the remaining scope/transfer gaps. The token rule is in a non-cooperative
  regression barrier: explicit None is intentional there, not a discarded production token.

No unresolved caller-versus-owner decision remains. Native teardown acceptance is an explicit validation
gap, not a claim that it was exercised: a full MainWindow/native-poster disposal failure-injection test
would require the native host/player integration fixture, outside the requested no-GUI validation scope.
Existing successful terminal cleanup paths were wrapped rather than replaced with cancellation-time disposal.

## Regression evidence and limits

- Five new regression cases were run against unchanged product/fixture code FIRST: **5 failed, 0 passed**.
  Archive/Media/Rules still installed key contexts after EndLifetime; the fixture left its service usable;
  VideoCleanupWatch had no terminal disposable contract. All pass after the changes.
- The sidebar case was added separately and checked by restoring ONLY
  `ArchivePage.MediaSidebar.cs` from exact base `17db174fb` temporarily, with automatic restoration:
  **1 failed (sidebar still resolved `reader` after teardown), 3 passed**. It now passes.
- **248 affected tests passed, 0 failed, 0 skipped** on the final sources. Includes the six lifetime cases,
  three managed-gate tests, CTS non-cooperative/identity/registration coverage, decode scheduling, update
  service/simulation, tagging decisions, video lifetime/watch/observation, terminal gallery retirement,
  quick-tag popovers, selection tagging, bounded shutdown and export entry points.
- The completion-owned CTS aliases already passed their lifetime tests on base; it would be dishonest to
  invent a red runtime leak test for those. Their proof is unchanged adversarial assertions plus the analyzer
  now seeing the real ownership structure. Reflection-only source lookup was updated without deleting
  token, registration, exception or lifetime assertions.
- The native stream/handler and descriptor constructor-failure paths have deterministic scope/finally proof
  and analyzer coverage. Exhaustive allocation-failure/native-wrapper instrumentation is impractical in this
  targeted run; no OOM/fault-injection or native acceptance claim is made for them.
- The PR #36-only hover/scrub/ReaderHoverEnlarge/archive-quote findings are NOT in this unbaselined goal
  inventory. Those files exist on base but remain under the untouched historical baseline. No hover/scrub
  code was imported from the unmerged branch. This clears inherited goal debt, not that branch's own
  additional findings; it must capture again after integration.

## Complete original inventory and verdicts

Lines below are the **base capture** locations, not post-fix locations. Rows 1–21 are assembly
`MeowChan`; rows 22–42 are `MeowChan.DesktopTests`. Every row is resolved.

| # | Base file:line | Rule | Verdict / fix |
|---:|---|---|---|
| 1 | `Desktop/GalleryDecodeQueue.cs:14` | CA2213 | Fixed — Already completion-owned; retain cancellation capability only, pump keeps scoped source. |
| 2 | `Desktop/GalleryTagPanelSuggestions.cs:33` | CA2213 | Fixed — Already completion-owned; scoped async warm-up owns source, identity-checked cancellation capability. |
| 3 | `Desktop/GalleryVideoPlayer.cs:57` | CA2213 | Fixed — Replace deliberately retained semaphore with managed-only FIFO async gate; late stale commands still serialize. |
| 4 | `Desktop/GalleryVideoPlayer.cs:1754` | CA1001 | Fixed — Implement terminal IDisposable for timer owner; dispose after native cleanup, not cancellation. |
| 5 | `Desktop/GalleryViewer.Cards.cs:23` | CA1001 | Fixed — Expose existing terminal StopAsync as IAsyncDisposable; complete host ownership contracts. |
| 6 | `Desktop/GalleryViewer.cs:191` | CA2000 | Fixed — Router-owned registration, cleared with router disposal. |
| 7 | `Desktop/GalleryViewer.cs:193` | CA2000 | Fixed — Router-owned registration, cleared with router disposal. |
| 8 | `Desktop/GalleryViewer.cs:195` | CA2000 | Fixed — Router-owned registration, cleared with router disposal. |
| 9 | `Desktop/GalleryViewer.cs:200` | CA2000 | Fixed — Router-owned registration, cleared with router disposal (five loop iterations, one diagnostic). |
| 10 | `Desktop/Updates/UpdateService.cs:64` | CA2000 | Fixed — Explicit HttpClientHandler transfer to HttpClient(disposeHandler: true); finally releases failed transfer. |
| 11 | `Desktop/Views/Archive/ArchivePage.MediaSidebar.cs:247` | CA2000 | Fixed — Retain sidebar key-context installation; release at archive EndLifetime. |
| 12 | `Desktop/Views/Archive/ArchivePage.TransferExport.cs:30` | CA2000 | Fixed — Router-owned registration, cleared with transfer-export router disposal. |
| 13 | `Desktop/Views/Archive/ArchivePage.axaml.cs:141` | CA2000 | Fixed — Retain archive key-context installation; release at EndLifetime. |
| 14 | `Desktop/Views/Archive/ArchivePage.axaml.cs:945` | CA2000 | Fixed — Scoped SKManagedStream around codec; verified input remains separately owned. |
| 15 | `Desktop/Views/Archive/ArchiveTransferExportDialog.cs:55` | CA2213 | Fixed — Already completion-owned; window retains cancellation capability, operations keep using/identity-checked release. |
| 16 | `Desktop/Views/Media/MediaPage.Selection.cs:391` | CA2000 | Fixed — Scoped QuickTagSession spanning popover completion, including pre-open failure paths. |
| 17 | `Desktop/Views/Media/MediaPage.cs:182` | CA2000 | Fixed — Retain media-page key-context installation; release at EndLifetime. |
| 18 | `Desktop/Views/Rules/RulesPage.axaml.cs:21` | CA1001 | Fixed — Expose idempotent existing EndLifetime through IAsyncDisposable; no ownership transfer to callers. |
| 19 | `Desktop/Views/Rules/RulesPage.axaml.cs:64` | CA2000 | Fixed — Retain rules-page key-context installation; release at EndLifetime. |
| 20 | `tests/Shared/Apphost/ApphostIntegrityProcessSmoke.cs:32` | CA2000 | Fixed — Exception-safe descriptor ownership transfer; finally disposes on parse/validation/constructor failure. |
| 21 | `tests/Shared/Apphost/ApphostPluginWorkflowSmoke.cs:21` | CA2000 | Fixed — Exception-safe descriptor ownership transfer; finally disposes on parse/validation/constructor failure. |
| 22 | `tests/MeowChan.DesktopTests/CtsDisposalTests.cs:38` | CA2000 | Fixed — Scoped test-owned child CTS; assertions still prove work disposed it BEFORE scope exit. |
| 23 | `tests/MeowChan.DesktopTests/CtsResourceOwnerTests.cs:46` | CA2016 | Fixed — Explicit CancellationToken.None for deliberately non-cooperative test barrier; forwarding cancelled worker token would invalidate the test. |
| 24 | `tests/MeowChan.DesktopTests/CtsResourceOwnerTests.cs:127` | CA2000 | Fixed — Scoped explicit HttpClient local; UpdateSession retains its documented transport ownership. |
| 25 | `tests/MeowChan.DesktopTests/GalleryFitDecodeTests.cs:42` | CA2000 | Fixed — Scoped test-owned decode queue. |
| 26 | `tests/MeowChan.DesktopTests/GalleryFitDecodeTests.cs:76` | CA2000 | Fixed — Scoped test-owned decode queue. |
| 27 | `tests/MeowChan.DesktopTests/GalleryFitDecodeTests.cs:101` | CA2000 | Fixed — Scoped test-owned decode queue. |
| 28 | `tests/MeowChan.DesktopTests/TaggingDecisionLogTests.cs:208` | CA2000 | Fixed — Scoped default-depth TaggingWalk; original depth assertion retained. |
| 29 | `tests/MeowChan.DesktopTests/UpdateServiceTests.cs:94` | CA2000 | Fixed — Scoped restarted UpdateService; fixture now also disposes its primary service. |
| 30 | `tests/MeowChan.DesktopTests/UpdateServiceTests.cs:109` | CA2000 | Fixed — Scoped restarted UpdateService; fixture now also disposes its primary service. |
| 31 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:27` | CA2000 | Fixed — Scoped simulated UpdateService. |
| 32 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:40` | CA2000 | Fixed — Scoped restarted non-simulated UpdateService. |
| 33 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:50` | CA2000 | Fixed — Scoped simulated UpdateService. |
| 34 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:60` | CA2000 | Fixed — Scoped prerelease UpdateService. |
| 35 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:81` | CA2000 | Fixed — Scoped UpdateService for real-check fallback. |
| 36 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:94` | CA2000 | Fixed — Scoped simulated UpdateService. |
| 37 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:108` | CA2000 | Fixed — Scoped real UpdateService spanning check and skip. |
| 38 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:109` | CA2000 | Fixed — Scoped real UpdateService spanning check and skip (same owner as previous row). |
| 39 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:111` | CA2000 | Fixed — Scoped simulated UpdateService. |
| 40 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:122` | CA2000 | Fixed — Scoped disabled UpdateService. |
| 41 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:123` | CA2000 | Fixed — Scoped automatic-checks-off UpdateService. |
| 42 | `tests/MeowChan.DesktopTests/UpdateSimulationTests.cs:134` | CA2000 | Fixed — Scoped simulated UpdateService for notifier test. |
