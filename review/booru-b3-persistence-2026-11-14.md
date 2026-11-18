# B3 persistence contract hardening — 2026-11-14

Branch: `codex/api-b3-current-20261114`, base `36514cc6`.
Worktree: `/home/tomka/.cache/claude-ssd-tmp/goal-api-b123-20261114/api-b3/source`.

## Scope and inputs

Read `Archive/BooruCatalog.cs`, the current Danbooru watcher domain and production
`DanbooruWatcherRecordMapper`, SDK/host claim contracts, the Danbooru parser, profile
lease/admission and regular-file readers, existing catalog/restart/corruption tests,
`booru-b2-boundary-2026-11-12.md`, the B4 queue review, B9 gates, the test matrix and
`docs/releases/v1.1.0-booru-data.md`. There is no `AGENTS.md` or
`.architecture-sparring` in this worktree or the checked enclosing worker directories;
no root-checkout fallback was used. No separate `ReadMapper` exists here: the
production watcher projection is `DanbooruWatcherRecordMapper`.

Only B3 product code and its tests changed. B2 domain/mapper, B1 transport, B4 queue,
UI, existing profiles and the root checkout were not changed. No live provider
requests, additional workers, service/account changes, merge, push, tag or release.

## Fixes

1. **Do not reject an explicitly empty provider observation.** The host mapper
   permits null, empty and space-only `RawRating`/`SourceClaim`, but B3 reused its
   nonblank identity validator. A valid empty source (including clearing a previous
   source) therefore made persistence fail. These two inert-text fields now retain
   their exact content; presence-sensitive merge still distinguishes absence from
   null/empty. Existing length and control-character checks remain in force.
2. **Require exact watcher state names.** `Enum.TryParse` accepts numeric strings,
   comma combinations and surrounding whitespace even for a non-flags enum.
   Examples `"5"` and `"Active, Removed"` parsed as Removed but were stored verbatim;
   the string-based terminal successor guard then did not recognize them as
   Removed. Both `State` and `ResumeState` now require the canonical enum name.
   Invalid caller records write nothing; invalid offline records fail closed and
   cannot be overwritten by a subsequent mutation. No valid writer/mapper record
   needs conversion and schema 1 stays unchanged.
3. **Keep every write reopenable under the technical ceiling.** The no-growth /
   terminal-state allowance could exceed `MaximumDocumentBytes` by up to 4096
   bytes. Its shared calculation now clamps to the 128 MiB read ceiling. Lowered
   configured-limit behavior and state-change slack below that ceiling remain.

## Coverage added

33 test cases in the two new partial `BooruCatalogStoreTests` files:

- exact provider-text retention, explicit clearing, absence-preserving merge,
  control/size rejection;
- canonical watcher names on writes and offline reads;
- null nested elements, wrong shapes, lone surrogate escapes and numeric overflow:
  all throw `InvalidDataException`, including on attempted overwrite;
- complete post claims (presence flags, raw rating, all tag categories and media
  roles, dimensions, size, MD5, source, deleted/banned), every original intent,
  local asset/path, all finding kinds and observation times across lease restart;
- no catalog created by an absent-file read, unrelated profile bytes unchanged;
- the **existing production mapper**, not a new test-only projection: populated
  and empty baseline, captured-null fence, active check/run counter, paused manual
  check, durable cooldown, budget exhaustion, error, recovery and removal across
  actual store/lease restarts;
- all six admitted mutation APIs reject foreign, disposed and Exclusive tokens,
  and honor pre-cancellation without disk/cache changes;
- accepted write remains pinned through shutdown and caller token disposal;
  drain cannot finish before I/O exits, while fresh writes are rejected;
- fault after atomic replacement reopens the entire claim/finding/request intent,
  not a partial observation or the old cached index;
- write-allowance boundaries, including terminal slack at the technical ceiling.

Existing duplicate-key/identity, explicit-null-collection, required-key,
truncation, interrupted-replacement, old additive-key and startup fail-closed
coverage was retained and rerun.

## Execution

Every build/test command ran under
`flock /home/tomka/.cache/claude-ssd-tmp/heavy.lock`.

1. Compile-first:
   `dotnet build tests/MeowChan.Tests/MeowChan.Tests.csproj --nologo -v minimal`
   — success, 0 errors, 45 pre-existing warnings.
2. Baseline Booru / Booru corruption / adversarial query selection:
   **647 passed, 0 failed, 0 skipped**.
3. New contract tests before the fixes: **8 failed, 7 passed**. Failures were
   the two empty/blank provider-text cases and all six watcher-state aliases.
4. Focused catalog/restart tests after the fixes: **155 passed**, no failures or skips.
5. Final build: `dotnet build tests/MeowChan.Tests/MeowChan.Tests.csproj
   --no-restore --nologo -v quiet` — success, 0 errors, the same 45 warnings;
   none in the changed catalog/test files.
6. Final test command:

   ```sh
   dotnet test tests/MeowChan.Tests/MeowChan.Tests.csproj --no-build --nologo \
     --filter 'FullyQualifiedName~MeowChan.Tests.Booru|FullyQualifiedName~DanbooruQueryAdversarial|FullyQualifiedName~PersistenceCorruption|FullyQualifiedName~ProfileAdmission|FullyQualifiedName~ProfileProducerAdmissionDrain|FullyQualifiedName~ArchiveCatalogAdmission|FullyQualifiedName~SessionPersistence' \
     --logger 'console;verbosity=minimal'
   ```

   **1784 passed, 0 failed, 0 skipped** (35 s).

Local ignored logs: `artifacts/b3-contract-red.log`, `b3-green-targeted.log`,
`b3-final-build.log`, `b3-core-persistence-booru.log`.
`git diff --check` passed.

## Boundaries

- No full solution, Desktop, packaging or Release validation; no real published
  1.0.2 profile, downgrade, native UI or live-provider acceptance. This is not a
  release-readiness claim.
- The 128 MiB slack correction is checked at the production allowance function;
  the new test does not allocate a fully populated 128 MiB catalog. Existing real
  file tests cover oversized reads, configured write limits and failed writes.
  At the technical ceiling an enlarging terminal-state write must fail rather
  than create an unreadable catalog.
- Atomic replacement is still per catalog mutation, not a transaction with the
  watcher scheduler or shared queue. A fault after rename may report failure even
  though the complete new observation is durable; replay remains identity-based.
- The temporary file is flushed before rename; the containing directory is not
  separately fsynced. No physical-power-loss durability claim is added.
- Direct mapper calls were not hardened or changed; B3 validates records before
  publishing them. Host/provider validation and CDN admission remain outside B3.
