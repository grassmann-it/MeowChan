# W7 WorkState continuation — agreed per-store seam tests

## Status

Implemented six unskipped theory cases: `media`, `intent`, `state`, each with
pending-exclusive and shutdown-drain. No build, test, negative-control run or commit
was performed. This is source implementation, **not a PASS or red/green report**.

Tests depend on the Root-owned agreed internal `WorkStateStore.AdmittedWriteObserver`
(`Action?`, default null). Root invokes it inside RunAdmitted's already accepted
RunWithToken delegate, immediately before operation(), outside the admission monitor.
Observer exceptions must unwind the use pin. This worker changes no product source.

## Deterministic handshake

- Seed a synthetic temporary SQLite profile in Startup, then CompleteStartup.
- Acquire Ordinary, install per-store callback, start worker owning the URL fence.
- Callback signals entered only after actual outer-use acceptance, then waits on a
  release event. Bounded waits are failure limits, not scheduling evidence.
- Await entered; request pending Exclusive or BeginShutdown + DrainAsync; Dispose the
  borrowed token while the outer use remains accepted and gated.
- Clear observer property before rejection probes. Its in-flight callback retains its
  wait. Assert barrier pending, new selected access/store uses and fresh Ordinary
  rejected, seeded rows unchanged, worker not completed.
- Release callback; require successful mutation and actual Exclusive grant or Drain
  completion. Verify exact resource and media rows through fresh SQLite read
  connections. Repeat disposed-token probes and verify persistence unchanged.
- Intent uses generation 2; state retains generation 1; media increments Attempts.
  Intent test additionally checks no accidental media copy into the new generation.
- Finally release gate, dispose token, cancel/observe Exclusive, await worker, clear
  observer. No waits under a lease/resource monitor on the controlling thread, no
  sleeps, ThreadState, reflection, lease reacquisition or personal fixtures.

The callback is guarded against repeated invocation and installed only after seeding;
only the dedicated worker can trigger its first accepted invocation in this fixture.
All new-use probes happen after Dispose and must reject before callback/fence entry.

## Negative control: prediction only, for Root runner

The prior monitor-only negative-reproducibility claim is withdrawn and not restored.
These tests deliberately gate **before the outer operation delegate proceeds**, not
at the lease monitor. They make no claim of an observed blocked lease instruction.

For a future intentional control, Root can overlay old nested-admission product code
with the **same observer seam in RunAdmitted** and these tests. Expected behavior:
Dispose requests release while the outer pin is retained; release observer; old inner
RunWithToken rejects the released token; successful-result/persistence requirements
fail. Current W6-02 continuation cores should complete without reacquiring the token.
This causal prediction has not been executed or reproduced by this worker. Root must
bind any eventual evidence to its actual product freeze and identical seam.

## Exactly changed paths

- `tests/MeowChan.Tests/WorkStateContinuationReleaseRegressionTests.cs`
- `review/wave7-workstate-continuation-tests-sol.md`

No product files, RUNSTATE, Lease/snapshot files, private data or cleanup changed.
