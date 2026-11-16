# Gallery-error fresh-process decomposition

Base: `ba1842af4` (includes native-pointer fix `ef5de36a4`). This change only
provides helper entrypoints. **Program/registry wiring belongs to Mencius; workflow
scheduling belongs to Kuhn.** Every split selector must start a separate smoke
process; do not invoke the four helpers in sequence inside one process.

## Exact wiring contract

All methods are `internal static Task`, on `GalleryErrorInteractionChecks`, taking
one `MainWindow owner` argument.

| Proposed selector ID | Method | Exact old checks retained |
| --- | --- | --- |
| `gallery-errors-retirement-clip` | `RunRetirementClipAsync` | `CheckExternalTransportRetirementAsync`; `CheckNativeClipFeedbackAsync` |
| `gallery-errors-layout-roots` | `RunLayoutRootsAsync` | `CheckContextLayoutRootsAsync`: external root false/true × post overlay false/true; each uses footer heights 180/280/180, real OS pointer entry, terminal pointer/timer retirement assertions |
| `gallery-errors-notifications` | `RunNotificationsAsync` | `CheckNotificationTransportAsync`: overlay false/true × paused false/true; then `CheckImageNotificationAsync` |
| `gallery-errors-failure-reader-return` | `RunFailureReaderReturnAsync` | `CheckExplicitActualFailureAsync`; `CheckActualMediaUnavailableAsync`; `CheckShellFailureAndReaderReturnAsync`: paused false/true |
| Existing unsplit debug selector, unchanged | `RunAsync` | Entire historical sequence below, same fail-fast semantics |

### Unsplit historical sequence

1. External transport retirement.
2. Native clip/focus feedback.
3. Create verified audio/video clip and image fixtures.
4. Four layout/root combinations.
5. Four notification/transport combinations.
6. Explicit current-generation media failure on a player that decoded real frames.
7. Image notification.
8. Actual missing verified media.
9. Playing and paused shell failure/reader-return journeys.

The split moves image notification next to the notification matrix, but the
unsplit helper retains its exact position between explicit and missing-media
failure. Across split groups every old check executes exactly once, with the
same loop order within each matrix.

### Failure/decoder coverage is not replaced

`CheckExplicitActualFailureAsync` first requires real decoded frames, then calls
`SetMediaUnavailable` for the current generation and checks native cleanup,
neutral status, no stale playback/slideshow movement. This is the original
explicit shell-failure injection, **not** a claim of a newly induced codec fault.
`CheckActualMediaUnavailableAsync` deletes a manifest-bound asset and verifies
actual missing-media rejection, reporting, disabled seek, no automatic retry or
advance, and rejection of late notices after stop.

Both playing/paused shell journeys retain actual decoder/audio readiness,
`CheckRealShellPreferenceFailureAsync` (real settings publication IO fault,
failed raw task and retained draft, preserved native intent, explicit durable
retry) and `CheckRealShellReaderReturnAsync` (real reader-return path). Existing
real-frame/audio evidence requirements, unavailable-audio branch, decoder errors,
operation deadlines and hold intervals are unchanged. No invalid fixture is
caught to continue with a shortened suite. Every check implementation, including
the native-pointer routine, is byte-identical to the base.

### Independent fixture/lifetime ownership

Every entry retains the disposable-desktop authorization and configured pinned
video-fixture gates. Each invocation owns its diagnostic trace/timer and temporary
workspace, with the original finally cleanup. Retirement/clip and layout-root
checks need no decoder asset and no longer materialize unused media. Notifications
creates its own verified clip and image; failure/reader-return creates its own
verified clip and missing-media fixture. No static cached fixture, previous-group
window, decoder state, or profile is required. Existing per-check Fixture and
shell-profile lifetimes are unchanged. Trace setup/flush and all deadlines remain
unchanged; selected group failures propagate to the harness.

## Bounded contracts

Run from any directory (requires .NET 10 SDK and Git history containing the base):

```sh
python3 tests/gallery-error-decomposition/check_contract.py
```

The Python runner checks the entire implementation suffix against `ba1842af4`,
then extracts and compiles the **actual new entrypoint/orchestration source** in a
temporary dependency-free .NET console project. Test doubles record scheduling,
not GUI success. Contracts cover all five exact schedules, four notification
combinations, both reader states, fixture failures, fail-fast check failures,
cleanup on failure/success, per-entry authorization/configured-fixture gates,
non-decoder groups' independence from asset preparation, and invalid group values.
No application/plugin build, shared project edit, full suite, CI or native run is
part of these contracts.

Local result: **2/2 Python contracts PASS**; extracted C# scheduling/guard/cleanup
contracts PASS. Fresh-process selectors are not runnable until the shared owner
wires them. Residual native verification: execute all four selectors in separate
authorized macOS processes with the pinned fixture, require every mapped matrix
case and normal stage completion; ensure a failed group does not suppress launch
of other independent processes. The actual failing process must still report
failure, not catch-and-continue. macOS native passes are not claimed here.
