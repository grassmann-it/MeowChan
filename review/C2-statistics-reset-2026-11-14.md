# C2 — Statistics reset (2026-11-14)

- Base: `integration/ux-wave5-20261114` at `a8e9600454f1ff157d7cc6d0db260c9659ef6720`.
- Implementation commit: `920e9e243be349d230603ec1f394267028fe5ffb`.
- Scope: statistics smoke/fixtures and focused reset contract tests only. No product change.

## Finding and deterministic reproducer

The counter/history complaint is a **smoke timing-contract error**, not evidence of an incomplete
`IoStatsHub.Reset()`. Reset collects/rebases the monotonic transport counters, clears session/day/
retention totals, rates and the ring, and deletes the persisted file and backup. It intentionally
keeps the visible window's sampling claim/timer. Subsequent idle ticks therefore append **new zero
samples**. The asynchronous UI acknowledgement is not an atomic observation of the empty ring.
`Series.Count == 0` is invalid at that point; zero samples are not leftover traffic.

`StatisticsResetContractTests.ResetClearsEveryMetricAndNewIdleSamplesAreNotLeftoverHistory`
reproduces this without sleeps, scheduling races, transport requests or a real profile:

1. A private `IoManualClock` drives a 500-ms window claim and synthetic counters in all six metrics.
2. Sample traffic, add another unsampled increment, then reset. All counters/rates/sources are empty
   and the ring count is exactly zero immediately after Reset; the window timer still exists.
3. Advance the clock 1500 ms. The ring count is exactly **3**, with zero in every direction. This
   necessarily falsifies the old smoke condition despite a correct reset.
4. Add fresh increments and tick once. Session/today/retained totals contain **only** those increments;
   all five history arrays contain three zeros followed by the new rate. Old traffic never returns.

The shared harness contract rejects any surviving source, any of six counter metrics in any of
three periods, nonzero current rates and nonzero history in any of five directions (including an
older, non-final sample). It accepts newly sampled zeros. The native smoke deliberately waits for
an idle tick before checking this contract, rather than racing it or disabling sampling. It does
not wait for nonzero history to age out, skip assertions or reset the hub a second time. It also checks
that all three displayed page graphs have no traffic.

## Reset question / anchor

The `2fff92ee` change (present here via `3e416b43`) correctly anchors overflow Reset to the persistent
statistics toolbar, not the button in the closing More popup. Product code stays unchanged.
The smoke now also verifies, after native layout/focus settling, that More actually closed and
that the question remains open with Cancel focused in Advanced, Simple and Barebones.

The unmodified baseline run passed the racy counter assertion on this occasion, then failed in
`StatisticsReadabilityChecks.cs:67`: an open question in the secondary fixture had no focused Cancel.
A synthetic routed Click neither activates a secondary window nor focuses its trigger. The fixture
now explicitly activates its own host and focuses Reset before testing the real popup's safe-answer
focus. It does **not** force focus onto Cancel or weaken the focus/layout assertions.

## Validation

- Compile-first baseline: Debug smoke build PASS (0 errors; existing warnings).
- Focused-test build: PASS (0 errors; existing warnings).
- Updated Debug smoke build: PASS (0 errors; existing warnings).
- Targeted tests: **291 passed, 0 failed, 0 skipped**, including all **30** new reset contract cases.
  Filter: `FullyQualifiedName~StatisticsResetContractTests|FullyQualifiedName~IoStats|FullyQualifiedName~IoRingTests|FullyQualifiedName~StatisticsReadabilityTests|FullyQualifiedName~WorkViewDiagnosticsStatisticsTests`.
  Result: `test-results/statistics-focused.trx`.
- First updated native process (`fixed-smoke`): exit 0, every statistics assertion PASS (including one fresh idle sample,
  all three Reset surfaces and EN/DE readability). The outer runner correctly rejected my mistaken expected id
  `statistics`; this base reports `normal+MEOWCHAN_STATISTICS_ONLY`. This evidence is preserved, not rewritten.
- Full `statistics` smoke with the correct expected id (`fixed-smoke-confirmed`): **PASS, exit 0**.
  Advanced/direct, Simple/More closed and Barebones/More closed all retained the question on Cancel;
  the post-reset snapshot had **1 fresh zero sample**. All counters/rates/nonzero history and the
  displayed graph traffic were cleared. EN/DE compact/wide readability, Cancel/Escape/reset and
  unchanged graph/scroll layout all passed; no binding errors.
- `git diff --check`: PASS.

All builds used `/home/tomka/.cache/claude-ssd-tmp/heavy.lock` with bounded waits and build timeouts.
The tests used private HOME/TMP/XDG directories, cached/offline NuGet packages and synthetic fixtures.

Evidence is local/ignored under `artifacts/c2-statistics/`: baseline/fixed build logs, test results,
runner reports and native captures. The original baseline's independent fixture-focus failure is
preserved in `baseline-smoke/normal/output.log`; it is not represented as a reproduced native
counter failure. The counter race is reproduced deterministically by the manual-clock contract test.

## Re-run

Use a private HOME/TMP/XDG tree, an offline NuGet configuration with cleared sources and the
already populated `/home/tomka/.nuget/packages` package cache. In this worktree the ignored
`artifacts/c2-statistics/env.sh` and `NuGet.Config` capture the validation setup. After sourcing it:

```sh
flock -w 600 /home/tomka/.cache/claude-ssd-tmp/heavy.lock timeout 300 \
  dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -m:1 \
  -p:UseSharedCompilation=false -p:NuGetAudit=false -p:RestoreConfigFile="$ROOT/NuGet.Config"

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~StatisticsResetContractTests'

flock -w 600 /home/tomka/.cache/claude-ssd-tmp/heavy.lock timeout 300 \
  dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj --no-restore \
  -m:1 -p:UseSharedCompilation=false
MEOWCHAN_STATISTICS_ONLY=1 python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll \
  --mode normal --expect-scenario normal+MEOWCHAN_STATISTICS_ONLY --timeout 180 --output "$ROOT/new-smoke-evidence"
```

## Boundaries

Linux/private Openbox/Xvfb only; Debug compile and targeted tests, no full-suite or release-readiness
claim. No Windows/macOS/native-user-desktop acceptance. No live provider requests, personal profiles,
auth stores or databases. Product reset I/O-failure/concurrent-profile-switch semantics are unchanged;
this is not a general reset/persistence audit. No theming/footer/gallery-layout or Booru-family slice
changes. No push/tag; this work did not change root/goal or integration references.
