# GUI CI collection contract

Requires shared smoke registry/dispatch commit `98f6dc213` (integrated in
`b52a96b14`). No full-reader fallback: an absent required leaf fails closed.

## Default `all` and `gui-only`

The collector requires 33 exact registry receipts, each once per platform:

- Nine existing normal scopes, unchanged.
- locked-profile, no-tray, tray-failure, download-session, selection.
- Reader: video-lifecycle, window-minimum, reader-navigation,
  reader-owned-navigation, reader-gallery-roundtrip, reader-list-gallery-return,
  reader-returns, reader-stability, reader-groups, gallery-close.
- GIF: gif-playback-core, gif-successors (both WebM and MP4),
  gif-verified-failures, gif-reader-association, gif-ownership.
- Gallery errors: gallery-errors-retirement-clip, gallery-errors-layout-roots,
  gallery-errors-notifications, gallery-errors-failure-reader-return.

The dedicated reader-media-input step remains independently required. Other
existing dedicated GUI checks retain their assertions, evidence and deadlines.
The unsplit `reader-ux`, `gif-playback`, and `gallery-error` chains are not in the
default inventory. Explicit focused `gif-playback` / `gallery-errors` requests
retain their old whole-chain reproductions and original worker cardinality;
selection/gallery-features/media-input likewise retain focused cardinality.
`gui-only` uses eight native / four non-native workers, without offline tests.

Native workers use deterministic groups (primary has one collector scene to
leave room for release/upgrade work). Four-lane platforms use stable longest-
declared-GUI-budget-first assignment, including dedicated check costs. Every
worker gates its own collector, prerequisites and assigned dedicated steps.
The existing matrix and release dependency require every worker to succeed.
The offline planner is not modified. No solution builds or matrix jobs added;
the smoke harness is restored/built once per participating worker, before capture.

## Bounds and honest limits

App bounds remain 300s for native macOS; on Windows/Linux normal/reader modes
remain 180s, while each split GIF/gallery-error leaf retains 300s. Existing
other dedicated checks keep 300/240/180s. Nothing increases the 75-minute job.

For the present inventory, **declared app deadlines plus the existing offline
step bounds only**, in worker order, are:

- macOS native 1..8: **27, 56, 55, 52, 47, 42, 47, 47 minutes**.
- Windows/Linux lanes A..D: **71, 73, 72, 73 minutes**.

These numbers EXCLUDE restore/build, fixtures/upgrades, desktop setup, runner
capture/cleanup, audio startup and artifact upload. **They are not a guarantee
that an all-timeouts run fits 75 minutes**, especially on the four-worker
platforms. Do not weaken app deadlines or claim otherwise. Actual per-scene
`duration_seconds` will inform subsequent measured balancing.

`scene-shard.json` records the inventory, assignments, source and budget model.
Registry discovery has a 120s bound. Each runner has an outer app+45s deadline
covering capture-lock wait, setup/capture and runner shutdown; private Linux
audio adds 45s startup allowance. On expiry, allow 20s graceful termination,
then bounded owned-runner escalation (at most 10s taskkill + 5s wait). An outer
expiry quarantines the worker regardless of any late receipt. Separate app
process groups cannot be assumed dead merely because their runner exited.

## Failure collection and safety

Ordinary assertion/app failures with valid cleanup receipts continue. Known
Popen/pre-audio-child launch failures also continue. A missing/malformed receipt
after launch, explicit cleanup error, outer expiry or forced termination marks
`unsafe-cleanup`: remaining assigned scenes are recorded as blocked failures.
Later GUI steps and upgrade/build work run a quarantine preflight. Independent
matrix workers continue, but incomplete/blocked/failed coverage cannot pass.
Linux gallery-error leaves each retain private functional-audio ownership,
forwarded selectors/fixtures and cleanup/readiness evidence.

`runner.log` is created before child launch. Incremental `collection.json` starts
incomplete; discovery errors also leave a failure report. Existing `always()`
upload includes `artifacts/desktop-smoke/**`, so per-scene app logs, runner logs,
private-audio reports, shard plans and partial/error collections are retained.
As with any Actions job, a hard platform/job cancellation can prevent later
steps from running; `always()` is not a guarantee beyond platform termination.

Targeted contracts simulate scheduling/receipts/cleanup failures; they do not
prove native rendering, decoder behavior, physical audio or runtime correctness
of the new C# helpers. Those require the corresponding native receipts.
