# Booru B2/B3 boundary review — Danbooru domain / watcher / persistence

**Date:** 2026-11-12 · **Branch:** `claude/booru-b2-boundary-20261112` (base `3c675786`)
**Reviewer:** independent Sonnet 5.5 worker · **Scope:** read-only review of product sources, one new test file.
**Inputs read:** `AGENTS.md`, `.architecture-sparring/config.json` + `index.md` and the accepted plan
`docs/architecture/gesamtprogramm-2026-10-31/pakete/1.1.0-booru-arbeitsplan-2026-11-12.md` (these three
are untracked in the root checkout, not in this worktree; read from root, no write), plus
`DanbooruPostReference.cs`, `DanbooruTagQuery.cs`, `DanbooruWatcherDomain.cs`, `BooruOriginalDownload.cs`,
`Archive/BooruCatalog.cs`, `Registry*.cs` diff, `plugins/Official/Danbooru/Danbooru.cs`, existing tests.
No live network; synthetic fixtures only.

## Verdict

B2 domain is solid on identity, query purity, baseline and state-machine rejection paths. **Two MUST
defects remain before B5 can rely on it**, both reproduced with synthetic pages (tests below, skipped
so the suite stays green). B3 storage is fail-closed/atomic/additive as specified, but its watcher
record is **not a lossless projection of the domain aggregate** (MUST-2).

## Findings

### MUST

**MUST-1 — Pages 2..5 of a catch-up never emit new posts** (`DanbooruWatcherDomain.cs:209-217`).
`ApplyPage` decides `newer` against `watcher.HighWaterMark`, but page 1 of the same traversal has already
raised it (`:219-222`). Repro: baseline `100`; check pages `[120,119]`, `[118,117]`, `[101,100]` →
emitted `["120","119"]`, expected `["120","119","118","117","101"]`. Posts 118/117/101 are put in
`KnownPostIds` and then never downloaded (silent loss; "no false completion" violated at the
result level). The existing tests only use ≤1 new page per run, so they do not catch it.
Fix direction (not applied, product scope): compare with `CheckStartHighWaterMark` (the stable fence the
type already carries) when set, falling back to `HighWaterMark` only for the first page of a run.

**MUST-2 — `BooruWatcherRecord` drops restart-critical state** (`BooruCatalog.cs:~127-140`).
Persisted: State, HighWaterMark, NextPage, BaselineEstablished, KnownPostIds. Not persisted:
`Progress.CursorBeforePostId`, `CheckStartHighWaterMark`, `ResumeState`, `PageSize`, `PageBudget`,
`Progress.BudgetLimited`. A watcher rebuilt from the record mid-traversal has `NextPage>0` but no cursor, so
`BeginCheck` sets `CheckStartHighWaterMark = HighWaterMark` (already advanced) and the next page hits
`reachedKnownBoundary` immediately → **state `Active`, traversal declared complete after one page**
(false completion after restart). Also a Paused-during-Checking watcher resumes as Active. Repro in
`WatcherRebuiltFromPersistedFieldsOnlyDoesNotFalselyCompleteAPartialTraversal`. No domain↔record mapper exists
yet (grep: only the domain, catalog and tests reference both types), so this is cheap to fix now, expensive
after a schema-1 file is shipped. B3 must add the fields additively (nullable, schema stays 1 or
bump with migration) and validate consistency (`NextPage>0 ⇒ cursor and checkStart present`).

### SHOULD

1. **Numeric-page traversal vs. cursor fence (B5 risk).** The adapter calls `posts.json?...&page=N&limit=L`
   (`Danbooru.cs:48`) and `BooruSearchRequest` has no cursor field. The domain exposes
   `RequestedCursorBeforePostId` and rejects any id `>=` the cursor (`cursor_did_not_advance` → `Error`).
   On a live tag, one post inserted between two page fetches shifts the last id of page N onto page N+1 →
   spurious `Error`. Fail-closed (no loss) but flaps for busy tags. Needs `b<id>`-style paging in the
   SDK/adapter or tolerance of an equal-to-cursor overlap id in the domain.
2. **Known-set bound mismatch.** Domain `KnownPostIds` is unbounded; store rejects `>100_000`
   (`MaximumKnownPostIds`). A long-lived busy watcher will eventually become unpersistable
   (`ArgumentException` in `UpsertWatcher`). Define pruning (e.g. keep ids above a floor tied to HWM) in B3/B5.
3. **`BeginCheck` accepts `Error` without `manual`.** A scheduler that polls `BeginCheck` retries Error
   watchers automatically, defeating "Error needs user/Resume". B5 must gate on state; consider rejecting
   `Error` unless `manual`.
4. **`UpsertPost` replaces the claim wholesale.** A later thinner observation (fewer `Fields`, e.g. a search
   row without tags) overwrites a richer earlier claim. Merge by presence flag or document last-write-wins.
5. **Original admission does not pin the CDN host.** `BooruOriginalDownloadSelector` and the catalog only
   require https/default port/no userinfo; host allow-listing lives elsewhere (plugin/B1). B4 must keep the
   host pin on the real download path; do not rely on this selector alone.
6. **Finding id uses `|` as an unescaped join.** Practically safe (query grammar and watcher ids are
   validated, `kind` is a fixed enum) but fragile; length-prefix or hash if the query grammar widens.

### Verified OK (acceptance matrix)

| Item | Result |
|---|---|
| Canonical provider post identity, URL boundaries | OK. Numeric id ≤18 digits, no leading zero, https + exact host, no query/fragment/userinfo, only `/posts/<id>[.json]`; id compare is overflow-free. No URL/byte dedupe substitute. Catalog requires `PostUrl` ↔ `PostId` match. |
| Query = exact user query, no `g,s`/private-tag injection | OK. `DanbooruTagQuery` never injects rating/order; catalog stores no private tags; findings/watchers require canonical query. |
| Baseline "from now", no backfill | OK. First check emits nothing; empty baseline then first post is new (new guard test). |
| Bounded paging 5×20, cursor progress, dedupe | OK in domain (`MaximumPageSize/Budget=20/5`, store `NextPage≤6`); numeric duplicate suppression via known set. **But see MUST-1/2.** |
| States Active/Paused/Checking/Cooldown/Error/Removed | Transitions correct incl. cancel/error/restart keep cursor and HWM (new guard test), Removed terminal on every entry point (new guard test). Persistence gap = MUST-2. |
| Several watcher findings → one post identity; one-shot ≠ watcher | OK. Finding id includes kind/watcher/query; single post record; no watcher record created by `Search`/`OneShot` kinds. |
| Fail-closed, atomic, additive persistence, offline read | OK. Separate `booru-catalog.json`, absent file = empty (migration), malformed/newer schema/oversize → `InvalidDataException`, temp file + `FileOptions.WriteThrough` + flush + `File.Move(overwrite)`, store-writer admission, startup `Read()`. |
| Provenance without chan-thread forgery | OK. Source/post ids are Booru-typed; no chan board/thread fields; local asset link requires stored post; sample/preview never promoted to original. |
| Provider metadata vs private tags/ratings | OK. Provider rating/tags/score live in the claim with presence flags; no coupling to the private tag store. |

## Tests

New file `tests/MeowChan.Tests/Booru/DanbooruWatcherBoundaryReviewTests.cs` (tests only; no product,
Archive, Tags, Downloader or UI change):

- 3 green guards (cancel/error/restart keep cursor; empty baseline then first post; Removed terminal),
- 2 `Skip`ped contract tests for MUST-1/MUST-2. Verified by temporarily removing `Skip`: both fail as
  predicted (`Actual: ["120","119"]` vs expected 5 ids; `Expected: Checking, Actual: Active`).
  Remove the `Skip` argument together with the product fix.

Commands and results (this worktree, Debug, net10.0):

```
dotnet test tests/MeowChan.Tests --filter "FullyQualifiedName~MeowChan.Tests.Booru|FullyQualifiedName~DanbooruQueryAdversarial"
  → Passed 211, Failed 0, Skipped 2, Total 213   (before adding my file: 208 passed)
dotnet test tests/MeowChan.PluginSdk.Tests --filter "FullyQualifiedName~Booru"
  → Passed 53, Failed 0
dotnet test tests/MeowChan.PluginRuntime.Tests --filter "FullyQualifiedName~Booru"
  → Passed 30, Failed 3 (BooruRealWorkerTests ×3)
```

The 3 PluginRuntime failures are an environment precondition, not a regression: they assert
`File.Exists` for Release builds of the Danbooru plugin and worker (`BooruRealWorkerTests.cs:22-23,60,87`),
which are not built in this worktree. I did not build Release (outside scope); they need
`dotnet build -c Release` of `plugins/Official/Danbooru` and `MeowChan.PluginWorker` first.
Static contract check was a source read only (no analyzer run beyond the normal build).

## Open integration risks for B4/B5

1. Fix MUST-1 and MUST-2 before B5 wires the scheduler; otherwise watchers silently under-report and
   falsely complete after restart. Both are in the two files the other B3 worker owns, so I did not touch them.
2. Cursor-based paging needs an SDK request field (B1/SDK contract) — decide before B5.
3. Define known-set pruning and the Error-retry policy (SHOULD 2/3).
4. B4 original admission: host pin + `MediaIdentifier = booru-<source>-<post>` must be the only queue
   identity so that a post found by N watchers yields ≤1 active download; link the asset via
   `LinkLocalAssetAdmitted` only after hash/size verification.
5. Persist watcher record and finding atomically per check (page result → post upsert → finding → watcher
   record) or define idempotent replay; today each store call is one atomic file write but the sequence is not,
   so a crash between "finding written" and "watcher advanced" re-emits the same ids (safe because findings and
   claims are idempotent, but the queue admission must dedupe by `MediaIdentifier`).

## Handover to the main checkout

- Apply: cherry-pick the single commit on this branch (report + test file). Nothing else changes.
- Hand MUST-1/MUST-2 to the B3/B5 owner; when fixed, un-skip the two tests in the same commit.
- No push, tag or release was performed.
