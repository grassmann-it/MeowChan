# Portable Desktop four-lane offline coverage

Only `MeowChan.DesktopTests` is repartitioned. Class-LPT uses the existing
checked-in timing profile, complete native discovery, and exact FQN settings.
`partition(..., shard_count=4)` leaves native macOS's default eight partitions
and original plan digest unchanged. The portable digest additionally binds four.

## Execution and ownership

| lane | Desktop cell | Unchanged other project owners |
| --- | --- | --- |
| a | 1/4 | ImageHeader, MetadataFilters, OriginalMediaHttp |
| b | 2/4 | Core, OfficialPlugins, ExamplePlugins |
| c | 3/4 | PluginRuntime, PluginCompiler, PluginHost.Configuration, PluginSdk |
| d | 4/4 | PluginFilters, SourceAdapter |

Four existing workers per selected Windows/Linux platform; no extra builds.
The cell is queued first in the same executor as existing shard tasks, with a
maximum of two concurrently executing tasks/test hosts. Existing per-shard
sequential project execution is unchanged. No unfiltered Desktop replay.

Desktop discovery has a 60s cap **inside** its 1800s project/shard budget.
Heavy and light legacy shard/project budgets remain 1800s and 900s; cleanup grace
remains 30s. The workflow step remains 35 minutes and job 75 minutes. Advisory
historical Desktop-only four-bin sums are about 815s per cell (Astra's model),
not a native runtime proof or an all-timeouts guarantee. With two hosts, the
unconstrained sum/queue model on lane d reaches 3600s before cleanup; lanes b/c
reach 2700s. Therefore **complete coverage under every timeout sequence is not
possible inside the 2100s step**. To avoid the Actions timeout hiding the rest of
the job, portable lanes share an absolute 1800s execution deadline. Each queued
legacy shard inherits its remaining time when it starts; exhausted projects
receive explicit failed/not-run evidence without launch. Desktop discovery and
execution also share the remaining lane/project budget. The existing final five
minutes are retained for bounded cleanup, aggregate writes, and evidence upload.
Missing/skipped/timed-out work fails closed. This patch does not claim successful
coverage under all hangs or increase deadlines.

## Acceptance and focused retests

`desktop-test-shards.py` requires four lane receipts per selected platform and
checks full source SHA, platform/architecture, full local inventory digest,
cross-worker method/cardinality inventory shape, timing-profile/count-bound
plan, recomputed assignment, actual TRX definitions/results/counters, and the
union of methods exactly once. Local theory case IDs need not match another
machine; each must match that worker's own TRX. Existing explicit skip/deferred
row semantics are reused, without new skips. It also requires source-bound
lane aggregate receipts preserving the other twelve project owners and exactly
one Desktop cell per lane.

A focused `offline-retest` deliberately runs only its selected lane's Desktop
cell and unchanged other project owners. Its receipt is labeled
`desktop-four-lane-cell-not-full-proof`; the full aggregate gate does not run and
this dispatch cannot claim platform/candidate completeness. Default/all and
candidate runs require the four-lane gate; GUI-only/focused GUI modes do not.
Artifacts are uploaded with `always()`, including failed discovery/timeout
logs. Missing artifacts cannot satisfy the aggregate. Candidate admission
requires this gate in addition to the unchanged native macOS coverage gate.

Run without builds:

    python -m unittest discover -s tests -p test_desktop_four_lane_shards.py -v
