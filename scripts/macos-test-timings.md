# Native macOS test scheduling observations

`macos-test-timings.json` is an advisory, checked-in scheduling input, **not a
coverage manifest or a deadline**. All native workers and the final gate use the
same file from the requested source commit. The plan digest binds both inventory
shape and this profile. An absent/malformed profile or mismatching digest fails
closed. No live machine timing or random theory display text affects assignment.

## Planner

- Keep each class (including all its methods/theory rows) on one of the requested native or portable workers.
- Use the maximum observed summed test duration per class across the sources.
  Store integer milliseconds; tie-break by class name, then shard index.
- Assign the next heaviest class to the currently lightest worker (LPT).
- New classes use the profile's median observed per-case cost (97 ms in this
  revision). Added discovery rows cost at least that fallback or the historical
  class mean, whichever is larger. Removed rows do not reduce historical cost.
  Even zero-duration/skipped-only classes have positive weight. Deferred runtime
  rows contribute time but their shared testId counts as one discovery identity.
- The profile includes measured slower Core classes. Unmeasured classes use
  the same per-case fallback. See the [Validate CI guide](../docs/development/validate-ci-sharding.md)
  for the current measurement and worker counts.
- Coverage still requires the entire discovered inventory, exact method filters,
  every native worker, and the original TRX/timeout/cleanup checks. Timings from
  failed runs are useful observations but never make a failed run acceptable.

## Evidence and initial replay (2026-11-16)

Complete Linux/Windows desktop TRX files came from
`/tmp/ochan-{linux,windows}-lane-a-retest.zip` (source `99dce90ce...`). Native TRX
observations came from run **43928862043**, artifacts **14258408621** (Intel3),
**14259562352** (Intel4), **14259617366** (Intel7), **14259294495** (Intel8).
The failed Intel6 job **133516453772** supplied partial console timings only.
Each input's SHA-256 and observed duration are recorded in the JSON provenance.
Partial console sums are lower bounds, not extrapolated finished classes or
coverage evidence; every included class also has TRX identity counts.

Intel6 was still progressing when its unchanged 600-second suite deadline
expired. Its case-count assignment bundled expensive low-row classes:
`StabilityRunnerHostTests` alone contributed about **207 seconds across 23 rows**.
There was no completed desktop TRX, so it remains a failed worker.

Replay on Intel6's actual complete discovery inventory (**13,084 cases, 774
classes**) verified that the new plan covers every method exactly once and is
unchanged when inventory order is reversed. Predicted summed-duration loads:

| Shard | Former case-count plan (s) | Measured LPT plan (s) |
|---|---:|---:|
| 1 | 196.08 | 407.47 |
| 2 | 255.16 | 407.47 |
| 3 | 541.57 | 407.47 |
| 4 | 530.14 | 407.47 |
| 5 | 273.09 | 407.47 |
| 6 | 658.41 | 407.47 |
| 7 | 364.24 | 407.47 |
| 8 | 441.06 | 407.47 |

These are scheduling estimates, **not guaranteed wall times**: host throughput,
parallel execution, startup/discovery, partial observations, and platform-specific
costs differ. Native validation must still pass the existing deadlines. A future
class larger than the deadline needs diagnosis, not a longer timeout or a skip.

## Refresh

Save completed native TRX artifacts and refresh with the offline generator:

```sh
python3 scripts/update-macos-test-timings.py \
  --trx runNN-artifactAA-intel3=/path/to/desktop.trx \
  --trx runNN-artifactBB-arm4=/path/to/desktop.trx \
  --partial-console runNN-jobCC-intel6=/path/to/partial-console.log \
  --output scripts/macos-test-timings.json
```

Repeat `--trx` for every retained source (including broad desktop inventories so
partial-only classes have reference counts). The generator is order-independent,
uses maxima rather than letting fast/failed observations erase slower ones, and
records input hashes. Review the diff and planner tests before committing. Do not
refresh independently inside CI workers: they must agree on the immutable plan.
