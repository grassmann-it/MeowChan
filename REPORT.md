# B9 offline acceptance coverage and smoke orchestration review

Base: `9fadd720` (branch `claude/b9-gates-booru-registry`). Read-only review plus one test.
No live requests, no network, no builds or test runs (see "Not verified").

## Coverage map (scenario -> deterministic gate)

| B9 topic | Gate | Level |
| --- | --- | --- |
| Search -> Preview -> Queue | `BooruB9AcceptanceTests.S1_*` (exact wire request, no hidden rating filter, only chosen original queued); GUI `booru-workbench`, `booru-composition` | host/core + fake-port GUI |
| One-shot post link | `S3_*`; `DesktopTests` workbench flow | host/core |
| Watcher lifecycle | `S5_*`, `DanbooruWatcher{Domain,Schedule,Scheduler,Invariant,BoundaryReview}Tests`, `BooruWatcherRestartContractTests` | strong, unit to integrated |
| Dedupe (search + one-shot + 2 watchers) | `S4_*`, `BooruOriginalQueueTests.SearchOneShotAndTwoWatchers...`, `BooruOriginalQueueTests.CatalogCost` | strong |
| 429 / cooldown | plugin `DanbooruSourceTests` (Retry-After digits/date/cap/garbage), `S5_S9_Provider429...` (survives restart), `S9_*` 13-case matrix, `DanbooruWatcherScheduleTests` | strong |
| Pause / cancel / restart | `S6_*` (4 gates incl. shutdown-during-transfer F1), `BooruOriginalLifecycleHardeningTests`, `BooruOriginalCrashWindowTests` | strong |
| Offline restart | `S7_*`, `BooruOriginalQueueTests.OfflineMedia`, `BooruMediaProjectionTests`, GUI `booru-media` | host + GUI |
| Migration | `S8_*`, `BooruCatalogStoreTests.NewBooruFileIsAnAdditiveOfflineMigration...` | synthetic profile only |
| Xvfb / DBus | `scripts/run-desktop-smoke.py` private Xvfb/Openbox/DBus; `test_desktop_smoke_runner.py` (isolation, registry, retry, teardown); `desktop-smoke-registry.yml` nightly | runner tested, workflow never run |

Core/host coverage is thorough; I found no missing deterministic gate there.

## Gaps, prioritized

1. **Nightly registry is the only CI path for the three booru GUI journeys, and it is unverified.**
   `dotnet-desktop.yml` runs no booru mode; `desktop-smoke-registry.yml` says in its header it has never run on GitHub.
   Until its first run, Search->Preview->Queue GUI has no CI evidence. Action: trigger it once manually
   (`workflow_dispatch`, `only: "booru-workbench booru-composition booru-media"`) and record the result.
   *Partly addressed by the new test below, which pins their registry membership.*
2. **Stale `docs/development/booru-test-matrix.md`.** Rows 3-7 still say "queue admission / scheduler / pause-cancel
   *open*", but `BooruB9AcceptanceTests` and the queue/scheduler suites now cover them. Misleading for release sign-off;
   refresh rows to point at the S1..S10 gates.
3. **Real 1.0.2 profile migration (S8) is synthetic.** `Released102RestPackageUpgradeTests` is env-gated and not booru-aware.
   Need a deterministic checked-in fixture (or generated-in-test legacy layout) plus a byte-identity assertion over
   pre-existing files after first booru use. Bigger than a narrow test; needs the fixture generation decision.
4. **Process boundary needs release builds.** `BooruRealWorkerTests`/`BooruWatcherRealWorkerTests` are the only worker-process
   gates for 429/offline; confirm they are in a CI job that builds the plugin first, otherwise they are skipped silently.
5. **No GUI-level restart/offline-with-Xvfb journey for the watcher list** (workbench covers create/pause/remove, not a
   process restart with watchers shown). Covered at host level by S7; low risk, optional.
6. **Preview bytes** (S1 notes none loaded in core) remain a UI-only claim; no deterministic gate that previews never hit
   the network outside the bounded preview path beyond `booru-composition` ("no socket").
7. S2 (rating/tag display separation) is data-level only; display separation is a B6/B7 UI gate not yet named.

## Added

`SmokeScenariosTests.BooruGuiJourneysStayInTheNightlyRegistryAsStandaloneOfflineRuns` (3 cases, pure registry data, no window):
each of `booru-workbench`, `booru-composition`, `booru-media` must stay a standalone Root entry with its selector, no
`NightlySkip`, no `Requires`, no extra `NightlyEnvironment`, and a PASS claim that states "no live provider/network/socket".
This prevents the offline GUI journeys from being silently skipped out of the nightly sweep or made network-dependent.

## Not verified

The new test was not compiled or run (no heavy builds permitted); it only uses members already used by sibling tests
in the same file (`SmokeScenarios.All`, `Id`, `Kind`, `Selector`, `NightlySkip`, `Requires`, `NightlyEnvironment`, `Pass`).
Run: `dotnet test tests/MeowChan.DesktopTests --filter BooruGuiJourneys`.
