# Wave 8 startup-fix final review

**Review type:** bounded, independent static review (not Curated tests)
**Date:** 2026-11-03
**Scope:**

- `Desktop/DesktopSessionHost.StartupPreview.cs`
- `Desktop/StartupPreviewStore.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewResponsivenessRegressionTests.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewSpecialFileRegressionTests.cs`
- `tests/MeowChan.DesktopTests/StartupPreviewUnsupportedPlatformTests.cs`
- `review/wave8-startup-preview-sol.md`
- `review/wave8-startup-fix-sol.md`

Supplementary source context was read only where required to resolve the requested questions: the ordinary `DesktopSessionHost` startup/shutdown path, its projection publication, `MainWindow` startup-preview handling, `Registry` restore, `RegularProfileFile`, and `ProfileLease`. No code or test files were changed. No build, test, Git, state, run-state, or snapshot command was run.

## Executive verdict

The fix closes the previously identified **product-boundary P1**: an optional startup-preview read that encounters `PlatformNotSupportedException` is now treated as a cache/display failure and cannot prevent the ordinary startup restore from being attempted. The source also preserves the important cancellation and state-publication guards.

The cache remains display-only. It does not become a source of watch identity, URLs, commands, or authoritative projection state. Ordinary restore still owns the real state, and the tests exercise that distinction with a real session-store wrapper in the relevant cases.

The old responsiveness-probe lifetime problem is improved: after the bounded probe wait fails, the fixture-owned gates are released and the actual probe task is awaited before the fixture is disposed. That prevents the known gate-induced probe from continuing to access disposed fixture resources.

There is nevertheless one material unresolved **test-harness P2**: the post-timeout probe drain is unbounded, and the checked-in runner has no default outer process/testhost timeout. If a blocked accessor is not released by the two known fixture gates, the test can remain stuck before cleanup and before the original assertion is rethrown. This is not evidence of a new startup product failure, but it means the responsiveness regression gate is not independently self-bounded.

No new P0 or P1 product defect was demonstrated by this static review.

## Findings

### P2 — The responsiveness regression test still depends on an external outer timeout

`StartupPreviewResponsivenessRegressionTests` starts the probe on a separate task and waits for it with a 15-second `WaitAsync(Deadline)`. In the timeout/failure path it now:

1. releases both fixture-owned gates;
2. awaits the actual probe task;
3. rethrows the original failure while deliberately observing any secondary probe exception.

This is the correct lifetime ordering for the known test scenarios: the probe cannot outlive the host/client/temp-file fixture merely because `WaitAsync` elapsed. However, the second step is an unbounded `await probe`. If the simulated blocked accessor is waiting on something other than the two released gates, the test never reaches its `finally`/fixture cleanup. The safety improvement therefore trades an orphaned task for a possible indefinitely waiting test process.

The inspected `artifacts/goal-runs/2026-11-01-profile-a/run-command.py` and `run-isolated.sh` do not install a default elapsed-time watchdog; `bwrap --die-with-parent` only handles parent death. There is no test-level timeout attribute or equivalent timeout in the inspected desktop test project. A caller could supply an external `timeout`, but no such invocation is part of the checked-in regression contract.

**Impact:** a malformed future probe or a new blocking path can hang the test host instead of producing a bounded failure. This is a test containment/evidence issue, not proof that startup itself hangs.

**Recommended follow-up:** make the root test invocation carry an explicit finite process/testhost timeout with descendant termination, and keep serial execution for these hang-sensitive tests. If a self-contained bound is required, isolate the potentially uncooperative probe in a killable child process rather than disposing live host resources after a second arbitrary wait. Do not solve this by silently detaching the probe again.

### P3 — Special-file watchdog cleanup remains narrow and not fully self-contained

The Unix special-file tests deliberately cover only supported Unix 64-bit environments. Their FIFO watchdog waits five seconds, attempts a nonblocking write-only rescue to produce EOF, then allows up to ten seconds for the operation to drain. That is useful for the expected `open`-without-peer case and avoids leaving the normal expected operation live in the fixture.

The cleanup is not a general deadlock guarantee. If the rescue itself is invalid, the assertion can fail before the operation has been drained. If the second drain wait expires, the operation can still hold the profile lease; the first test's `using ProfileLease` cleanup then remains dependent on that operation releasing the monitor. The host fixture also has its own bounded disposal wrapper, but the timeout path still has cleanup work after that boundary. The same outer process watchdog recommended above is therefore required for this test family as well.

This is a test-evidence limitation, not a finding that the production `RegularProfileFile` handling accepts the FIFO. The tested path rejects the FIFO and the expected peerless open is rescued in the normal case. Windows named-pipe/reparse/device behavior remains untested by this file.

## Requested behavior checks

### Cancellation

The optional preview boundary checks cancellation before reading and again after the optional reader returns. `OperationCanceledException` is intentionally not swallowed by the broad optional-cache catch, so cancellation remains a cancellation outcome rather than being converted into a successful preview. The post-read and post-barrier checks prevent a canceled startup from publishing a preview after cancellation.

The stop path synchronously changes the host out of `Starting`, cancels the startup token, and drains entered restoration before releasing the registry/owner resources. The host's `Ready` and `RecoveryFailed` publication paths still require the lifecycle state to be `Starting`. Therefore a stop racing with the optional read cannot produce a late `Ready` or `RecoveryFailed` frame.

The important limitation is unchanged: the synchronous preview reader is cancellation-aware between operations, but cancellation cannot interrupt a regular filesystem syscall that is already stalled. The test suite simulates and bounds the known blocking cases; it does not turn synchronous I/O into a generally preemptible operation.

### Ordinary restore

The preview read is optional. After it completes or is rejected as an optional cache failure, the normal plugin/content/registry restoration path remains the source of truth. In particular, once ordinary registry restoration has entered its existing non-cancelable restore operation, stop drains that operation rather than pretending it was never entered. The tests that hold a real store wrapper verify that an ordinary successful restore can still provide the actual URL/title and that an entered restore is not replaced by a synthetic preview.

The unsupported-platform cases inject `PlatformNotSupportedException` only at the optional preview seam. They then verify both ordinary-restore success and ordinary-restore failure behavior. The unexpected `InvalidOperationException` case remains a real startup failure and is not incorrectly swallowed by the optional-cache fallback.

### Synthetic cache has no authority

`StartupPreviewSnapshot` contains presentation data such as labels/counts, not watch IDs, URLs, commands, or mutable handles. The host keeps `_startupPreview` separate from the authoritative projection and handle maps. The checkpoint path captures only an applicable, non-stale `Ready` projection; a preview cannot create that checkpoint.

The UI retires the preview when an authoritative frame is applied and hides it on close/failure. Retaining a historical preview object on the host is not equivalent to resurrecting watches. The tests also reject using preview-derived IDs for check/remove/retry, including real IDs from the entered restore rather than only a dummy sentinel.

This review does **not** treat that separation as a privacy/anonymization proof, nor as proof of UI-dispatcher timing. It establishes only that the preview data is not wired as an authority in the inspected paths.

### Probe cleanup

The fix is correct for the previously described fixture-gate failure: after `WaitAsync` expires, both gates are released before the actual probe is awaited, so the probe can finish while its host/client/temp-file dependencies are still alive. The nullable `Task.Run<Exception?>` form in the special-file test is also harmless compile-hygiene and does not change the production behavior.

The remaining unbounded drain described above is the unresolved boundary. The current code must be paired with an outer watchdog if the test run is expected to be operationally bounded.

### Outer timeout

No checked-in outer timeout was found in the inspected runner or desktop test project. The 15-second values inside the tests are not sufficient to claim a hard test-process bound because the cleanup await is outside that bound. This is the principal open item for the final handoff.

## Reconciliation with the previous reviews

| Earlier review item | Final assessment |
| --- | --- |
| Optional preview could fail startup on `PlatformNotSupportedException` (P1) | **Closed at the product boundary.** The optional read catches `PlatformNotSupportedException`; normal restore continues. Cancellation and unexpected `InvalidOperationException` remain distinct. |
| Cache/preview could become authoritative | **Closed in the inspected implementation.** Preview is display-only; authoritative projection/checkpoint and real restore remain separate. |
| Stop/cancellation could publish late startup state | **Closed for the inspected lifecycle races.** Cancellation is not swallowed, state checks guard publication, and shutdown drains entered restore. Blocking synchronous I/O remains inherently non-preemptible. |
| Responsiveness tests could leave an orphan probe using disposed fixtures | **Fixed for the known gate-induced path.** Gates are released and the actual probe is awaited before disposal. |
| Responsiveness regression is independently time-bounded | **Still open, P2 test harness.** The final probe await is unbounded and the inspected runner has no default outer watchdog. |
| Unix special-file/FIFO behavior | **Improved but narrow.** Expected FIFO rejection and fallback cleanup are covered on supported Unix; watchdog cleanup is not a general hang proof. |
| Windows special-file behavior | **Unchanged evidence gap, P2.** No native Windows named-pipe/reparse/device execution was performed or demonstrated by these tests. |
| UI dispatcher responsiveness and real startup latency | **Unchanged evidence gap, P2.** These are synthetic task/fixture tests, not native UI timing or a measured cold-start benchmark. |

## Test/evidence boundary

The three test files are meaningful synthetic regression coverage, but this was a source review only and none of them was run here.

- `StartupPreviewUnsupportedPlatformTests` covers six synthetic cases: optional PNSE with ordinary restore success/failure, stop/cancellation races, `SaveUrlsOnExit=false`, and an unexpected exception. It uses supported test infrastructure while injecting PNSE; it is not proof that the application can fully operate on an actually unsupported OS/architecture.
- `StartupPreviewResponsivenessRegressionTests` covers empty and real paused-watch restore cases plus failure/stop combinations, including rejection of actual restored watch IDs as preview authority. It does not establish a UI dispatcher latency budget.
- `StartupPreviewSpecialFileRegressionTests` covers the supported Unix special-file path and the expected peerless FIFO scenario. It does not cover Windows special files and its rescue is intentionally limited.
- No native Windows/macOS/Linux matrix, real UI automation, cold-start timing, or process-level timeout run is claimed by this review.

## Handoff / unresolved items

1. Add an explicit finite outer testhost/process watchdog with descendant termination to the root invocation used for these hang-sensitive regression tests. Record the command and resulting evidence in the next run; do not rely solely on the in-test `WaitAsync`.
2. Keep the responsiveness and special-file tests serial or otherwise isolated while they retain live-resource watchdog logic.
3. If the project requires a self-contained timeout rather than an external harness contract, move the potentially uncooperative blocking probe into a killable child boundary. Do not dispose a live fixture merely to satisfy a local timeout.
4. Run the desktop tests on the intended native platform matrix and separately collect UI-dispatcher/cold-start measurements before closing the remaining evidence-gap findings.

**Final disposition:** product P1 from the previous startup review is addressed in the inspected source. The implementation is suitable for the next bounded validation run, provided the run supplies the explicit outer timeout above. The outer-timeout dependency, narrow special-file watchdog, native-platform coverage, and UI/latency evidence remain unresolved and should not be marked complete from this static review alone.
