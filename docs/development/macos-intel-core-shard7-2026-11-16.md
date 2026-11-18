# macOS Intel Core shard 7: bounded historical tag-catalog benchmark

## Evidence and scope

Investigated run **43928862043**, job **133516453731**, artifact **14259617366**,
starting from `b7ec40869` in a separate worktree. Input artifacts:
`/tmp/ochan-intel-shard7.zip` and `/tmp/ochan-intel-shard7-failure.log`.
The ZIP's Core files were extracted to `/tmp/ochan-intel-shard7-evidence/shard-07/core/`.
No Desktop tests, production code, runner scripts, deadlines, or skip settings changed.

- `run/result.json`: exit 1 after **217.89 s**, runner timeout **false**,
  budget **287.404287049 s**, process cleanup complete, no remaining processes.
- `run/results/*/Sequence_c899d8f1c2204c929c49e30a22482836.xml`:
  **1,461 entries**, **1,460 completed**, exactly one `Completed="False"`:
  `MeowChan.Tests.Tags.PrivateTagStoreScaleEvidenceTests.ParseAndWriteTimingsAndBytesPerItemAgainstTheByteCap`.
- TRX: **1,460 results**, **1,458 passed**, two explicit skips, **zero failures**.
  The unfinished test has no result; those totals do not establish suite completion.
- `run/vstest-diag.datacollector.26-11-16_18-44-12_97466_6.log`:
  - line 14691: folder scan benchmark completed at **18:45:43.116**;
  - line 14696: the private-tag scale test started at **18:45:43.118**;
  - line 14697: the **2-minute inactivity** warning at **18:47:43.181**.
- `run/console.log` identifies the same test as running when the host was killed.
  The host diagnostic continues transport polling until the dump/kill, without a
  completion event for that test.
- `shard-07/truncation.json` reports both archived dump copies truncated from
  **32,010,616 bytes** to ZIP entries of **8,388,650 bytes**. They are not intact
  stack evidence. The artifact does not identify the exact executing phase within
  the test, and no finalizer or teardown deadlock is established.

## Narrow cause and correction

The outstanding test is synchronous scale/timing evidence, not post-suite cleanup.
Its `MeasureCatalog` deliberately replays the **former linear** `Contains` using
`Definitions.Any`, despite production `Contains` already using a dictionary.
Commit `562ebfec4` raised the catalog ceiling from 1,024 to 20,000; the historical
lookup benchmark still ran 150,000 lookups per pass, plus one warm-up and five
measured passes. At 20,000 definitions this requires exactly **8,700,450,000**
predicate comparisons. It is avoidable CPU work inside the unfinished test.

The unchanged test completes locally, with the historical loop taking a median
**3,638 ms per pass**. This supports an excessive benchmark-workload diagnosis,
not a demonstrated lifecycle deadlock. The macOS artifact alone cannot prove
which phase was executing at the kill.

Bound only the historical comparator to **256 deterministic, evenly distributed
positions**, including the first and last definition. All six passes together now
require **15,360,006** comparisons (about **566x** fewer). Report sample counts and
per-lookup timings explicitly rather than comparing unequal aggregate timings.

Keep all moderate/dense/ceiling document sizes, validation, parse/read repetitions,
four writes per document, 1,024 sequential definitions, full-catalog bulk/single
creation, and **150,000 indexed lookups per pass** unchanged. No benchmark phase or
test is skipped. Added five theory cases check small/full vocabularies, unique
ordered samples, endpoint coverage, spacing, and a **16-million-comparison** cap
across the six historical passes. This is a deterministic work bound, not a timing
assertion or a timeout increase.

## Local validation (Linux x64, SDK 10.0.111)

Builds were serial, with shared compilation and node reuse disabled:

```sh
dotnet build tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release \
  -p:UseSharedCompilation=false -nodeReuse:false -m:1
```

The focused test runs used the unchanged blame deadline:

```sh
dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj -c Release \
  --no-build --no-restore -p:UseSharedCompilation=false -nodeReuse:false -m:1 \
  --filter 'FullyQualifiedName~PrivateTagStoreScaleEvidenceTests|FullyQualifiedName~PrivateTagCatalogComplexityTests|FullyQualifiedName~PrivateTagStoreComplexityTests' \
  --blame-hang --blame-hang-timeout 2min --blame-hang-dump-type mini \
  --logger 'console;verbosity=detailed' --logger trx \
  --results-directory /tmp/ochan-shard7-fixed
```

- Baseline: only `PrivateTagStoreScaleEvidenceTests`, **1/1 passed**, test about
  **46 s**; log `/tmp/ochan-shard7-baseline.log`, TRX `/tmp/ochan-shard7-baseline/`.
- Fixed: **11/11 passed**, zero skips/failures, **33.7968 s** total; scale test
  about **29 s**, sampled historical loop median **29.9 ms**, indexed 150,000
  lookups median **0.7 ms**. Log `/tmp/ochan-shard7-fixed.log`, TRX
  `/tmp/ochan-shard7-fixed/`. Includes existing catalog equivalence and store
  parse/backup/full-vocabulary complexity regressions, not a broad suite.
- Build: zero errors (existing analyzer warnings remain).

These are local observations, not a macOS performance guarantee. No push or CI
run was performed; a macOS Intel rerun remains the confirmation of deadline margin.
