# B5 watcher scheduler boundary fixes — 2026-11-14

Branch: `codex/api-b5-current-20261114`, base `36514cc6`.
Worktree: `/home/tomka/.cache/claude-ssd-tmp/goal-api-b123-20261114/api-b5/source`.

## Scope and inputs

Read the B2 boundary review, B5 preflight, relevant B4/B9 handovers, and the current domain,
record mapper, catalog, scheduling selector, scheduler contracts, host, search port and queue admission.
`AGENTS.md` and `.architecture-sparring` are absent in this worktree; no instructions or architecture
files were fetched from the protected main checkout. The old B2 preflight blockers (stable traversal
fence, restart projection, pause target) are already implemented in this base and were not changed.

Product changes are confined to `DanbooruWatcherScheduler.cs` and `DanbooruWatcherHost.cs`.
No B2 domain/mapper, B3 catalog, B1 transport/SDK/plugin, B4 queue, Desktop or UI changes.
The existing scheduler contract and queue-admission APIs remain unchanged.

## Reproduced defects and fixes

1. **Late successful search after cancel/stop.** A port completion racing cancellation could establish
   a baseline or advance an otherwise unchanged page, because no new posts meant no admission-token
   check. Recheck cancellation after the search, before interpreting/committing page progress. Return
   to the existing resume target, retaining the last durable traversal data. A late provider deferral
   remains a durable provider fact rather than being discarded as an ordinary cancellation.
2. **Cancellation racing the final admission.** An admission could record its finding and then return
   successfully after cancellation. The scheduler still advanced the page. Recheck the cancellation
   boundary before the checkpoint write, just as for a profile-generation cut. Keep the admitted
   finding, cancel without advancing, and replay idempotently after restart. A host-path regression
   checks the same behavior through the real search facade/transport with an in-process provider.
3. **Faulted automatic loop spins.** A fault set after loop startup did not prevent automatic picks.
   Still-due watchers were selected, rejected by `Gate`, and immediately selected again, overwriting
   unrelated last-check summaries. A fault now drains queued manual callers with the stable fault
   code and parks selection. A direct `RunNextAsync` also answers already queued manual work after a
   fault was latched by a status read instead of leaving its task pending.
4. **Provider deferral lost on a transport-fence getter failure.** The deferral path directly called
   the transport getter. An exception replaced the received deferral with `check_faulted`, losing its
   durable retry fence. Use the existing guarded fence reader: latch the visible fault, persist a
   conservative bounded maximum cooldown, and retain restart protection for every watcher.
5. **Stop blocked behind startup's profile wait.** `StartAsync` held the host lifecycle gate while
   waiting for an open generation; `StopAsync` waited behind it without cancelling that wait. A
   host-owned startup cancellation source is now signalled before Stop waits for the gate. Stop is
   terminal, caller-only startup cancellation remains retryable, and no generation/service is opened
   or stopped on the caller's behalf.

The new core regression file adds 10 cases (including baseline/established and caller-cancel/stop
variants). Synthetic failed writes, late completions, replay and a fresh transport without remembered
cooldown exercise the actual B3 catalog boundary. The existing process-global logger-capture tests
now use the pre-existing nonparallel logger collection: otherwise the new concurrent fault-injection
warnings contaminated their exact event counts.

## Validation

All builds and test runs executed under:

```text
flock /home/tomka/.cache/claude-ssd-tmp/heavy.lock ...
```

Compile first, before tests:

```text
dotnet build tests/MeowChan.Tests/MeowChan.Tests.csproj --nologo -v minimal
dotnet build tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj -c Release --nologo -v minimal
```

Both succeeded. Existing unrelated warnings remain; no new compiler/analyzer warnings in the changed
code. Release is only the test build configuration needed by the real-worker fixture tests, not a
release action or release claim.

Baseline (before fixes): Core Booru **619 passed**, PluginRuntime Booru **256 passed**.
The new regressions reproduced the late writes, admission race, fault spin, lost deferral and parked
startup shutdown before the fixes. One initial deferral fixture needed deterministic creation times
so its assertion inspected the watcher actually selected; that fixture was corrected.

After fixes, the two build commands above succeeded again, then:

```text
dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj --no-build --nologo \
  --filter "FullyQualifiedName~MeowChan.Tests.Booru" -v minimal
# 629 passed, 0 failed, 0 skipped

dotnet test tests/MeowChan.PluginRuntime.Tests/MeowChan.PluginRuntime.Tests.csproj -c Release --no-build --nologo \
  --filter "FullyQualifiedName~Booru" -v minimal
# 257 passed, 0 failed, 0 skipped
```

Additional checks (also under the lock): the 16 boundary/observability cases passed three consecutive
`--no-build` repetitions; `--filter "FullyQualifiedName~BooruCatalogCorruptionTests"` passed all 28
cases, with no failures or skips.

This includes existing single-flight, periodic selection/pacing, individual/session/profile pause,
manual check, error/resume, cooldown/reset, offline restart, pending-original recovery, real queue
admission/deduplication and loopback real-worker coverage. Local logs are in ignored `artifacts/b5/`.

## Boundaries

- No live Danbooru/provider/CDN requests: in-memory fixtures/fakes and loopback only.
- No full solution/Desktop/UI/package/real-profile validation; only the specified core/runtime seams.
- Admission stays at-least-once across the checkpoint boundary; the existing B4 identity/finding dedupe
  supplies idempotency. No new queue, transaction, storage schema or provider-fence record was invented.
- An actual failed catalog write still cannot make new state durable. Such failures remain explicit
  faults/backoffs; this patch does not claim durability despite failed storage or an end-to-end release gate.
- Protected main checkout and `~/.oChan` untouched. No merge/reset/stash/push/tag/release, no account or
  service changes, and no additional implementation workers.
