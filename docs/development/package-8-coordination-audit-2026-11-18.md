# Package 8 — Coordination Audit

**Audit scope:** coordination only. No merge, product-code change, CI/infrastructure change, or test-policy change was made by this audit.

**Snapshot:** 2026-11-18 (Europe/Berlin)

**Audit worktree:** `/home/tomka/.worktrees/ochan-ledger/worker-8`

**Target branch:** `origin/goal/gesamtprogramm-profile-a`

**Current target head:** `866ad8ea24a09d57e9e8e5e3452d4de6beaa100a` (`fix: expose effective suggestion shortcut help`)

## Executive decision

> **PKG-8-COORDINATION-GATE: BLOCKED / NOT READY FOR A BATCH MERGE OF PACKAGES 1–7.**

The repository is not in a safely coordinated state yet. The blockers are coordination blockers, not a claim that every package implementation is wrong:

1. **Duplicate active PRs:** Package 6A is represented by both PR **#71** and PR **#73**, with the same review file; Package 9 is represented by both PR **#72** and PR **#74**, with the same audit file and test. One canonical PR must be retained for each pair and the duplicate closed or explicitly superseded.
2. **Stale/conflicting reader PR:** PR **#66** is an older draft for the same reader/media activation surface as Package 4A PR **#75**. GitHub reports #66 `CONFLICTING`; the two branches share all six implementation files modified by #75. #66 must not be merged independently.
3. **Unpublished worker state:** the current Package 1–7 worker worktrees are mostly still at the target head with no committed delta and no PR. A worker status is therefore not delivery evidence. The only auditable package deltas are the older/open PRs and the committed branch snapshots listed below.
4. **Stale base references:** except for PR #77, the active package/audit PRs inspected here were opened from the previous target snapshot `4077eb868`; the current target is `866ad8ea2`, one commit ahead. Each retained PR must be refreshed/rebased or its clean mergeability rechecked before merge.

The gate can be reopened only after the canonical PR map, dependency order, and overlap dispositions below are updated in the live PR set.

## Evidence baseline

| Item | Evidence |
|---|---|
| Target branch | `origin/goal/gesamtprogramm-profile-a` |
| Current target head | `866ad8ea2` |
| Previous common PR base | `4077eb868` |
| Delta from previous base | `866ad8ea2` — `fix: expose effective suggestion shortcut help`; touches `Desktop/Controls/QuickTagSurface.cs` and `tests/MeowChan.DesktopTests/ShiftLetterSchemeTests.cs` |
| Worktree status | clean before this documentation change; audit branch was created from the current target head |
| Merge policy applied | no merges, no rebases of other branches, no force-pushes, no source/CI edits |
| Checks performed | remote refresh, branch/ref inventory, PR metadata/file inventory, exact changed-file comparison, `git merge-tree` against current target head |

## Package 1–7 coordination inventory

The table distinguishes **current worker state** from **auditable committed/PR state**. A worker worktree at the target head is intentionally recorded as “no committed delta”, not as “done”.

| Package | Current worker lanes observed | Auditable branch / PR state | Coordination reading |
|---|---|---|---|
| **1 — Media selection/counter/sidebar** | `worker/1b-media-counter-shortcuts-20261118`, `codex/worker-1c-media-sidebar-title-20261118`; both at `866ad8ea2` with `0/0` delta | PR **#69**, `fix/pkg-a-media-context-contract-20261117`, head `f350ce2ca`; open and reported mergeable against its older base | Treat #69 as the only currently auditable Package-1 implementation candidate. Do not assume the two current worker lanes are delivered. Exact semantic adjacency to open PR #36 through `ReaderMediaSidebar.cs`; recheck after #36 is resolved. |
| **2 — Tagging actions/responsive layout** | `codex/package-2a-tagging-actions-20261118`, `codex/worker-2b-tagging-responsive-20261118`; both at `866ad8ea2` with `0/0` delta | No current Package-2 PR found. Related merged baseline: PRs **#62/#64** (UX-LEDGER-28 tagging packages); older local `codex/pkg2b-tagging-responsive-20261117` points at `4077eb868` with no unique delta | Package 2 has no current merge candidate. Reopen with one branch/PR per disjoint scope, based on `866ad8ea2`, and declare whether the work is a continuation of the already merged UX-LEDGER-28 packages or a new ledger item. |
| **3 — `CountFor(null)` / ungrouped queue** | `audit/package-3-countfor-null-verification` at `866ad8ea2` with `0/0` delta | PR **#76**, `codex/pkg3-countfor-null-20261117`, head `73bb24ead`; open; modifies `TagQueueItem.cs`, `TaggingPageMenuTests.cs`, and one ledger document | This is a coherent single PR. It is semantically adjacent to Package 2’s tagging surface but has no exact file overlap with PR #64’s changed-file set. Merge only after Package 2’s intended scope is declared, so the empty-state contract is not redefined twice. |
| **4 — Reader/gallery shortcuts and continue-reading** | `worker/4a-reader-shortcuts-20261118`, `codex/package-4b-video-gallery-20261118`, `worker/4c`; all at `866ad8ea2` with `0/0` delta | PR **#75**, `codex/pkg4a-reader-shortcuts-20261117`, head `bd52c5172d`; open; old draft PR **#66** is conflicting and overlaps it exactly in six implementation files | Canonicalize on the newer/smaller Package-4A candidate #75 unless the owner explicitly replaces it. Close/supersede #66 before any merge. Keep Package 4B/4C unclaimed until each has a committed delta and an explicit PR. |
| **5 — Booru B5–B10 / preview viewer** | `worker/5a-booru-b5-b10-audit-20261118`, `worker-5b`; both at `866ad8ea2` with `0/0` delta | No current Package-5 PR found. Historical merged PR **#16** covered the earlier UX-LEDGER-05 viewer; the B5–B10 work has prior local/remote feature history but no current Package-5 delivery candidate in the open PR list | Do not treat the historical viewer merge or a worker checkout as current B5–B10 completion. The package needs an explicit scope ledger (B5…B10), one owner branch, and one PR; it should avoid changing shared reader/media/shell files unless a contract dependency is recorded. |
| **6 — Localization plural/spacing/remaining string work** | `codex/worker-6a-localization-plurals-20261118`, `worker-6b`, `worker-6c`, `worker/6d-identical-localization-strings-20261118`; all at `866ad8ea2` with `0/0` delta | PR **#71** and PR **#73** duplicate Package-6A evidence; PR **#77** is the Package-6C spacing audit and is based on current head; no current PR for 6B/6D | Retain one 6A PR only. #73 is the more complete 6A candidate because it carries the additional diagnostics document; close/supersede #71 after owner confirmation. Keep #77 separate: it is documentation-only and has a different audit boundary. The 6A translation-authority blocker remains valid and must not be “fixed” by invented translations. |
| **7 — UX/UI ledger/alignment/plugin-card follow-up** | `worker/7a-ux-ui-ledger-20261118`, `worker-7b`, `codex/worker-7c-plugin-cards-color`; all at `866ad8ea2` with `0/0` delta | PR **#70**, `codex/ux-ui-ledger-7a-20261117`, documentation-only ledger capture; prior committed local Package-7B snapshot `b8e1a6692` modifies `ToolbarGapPatternTests.cs`; prior local Package-7C audit commit `615516915` is not an open PR | #70 is a ledger capture, not implementation acceptance. The eight new ledger IDs (`UX-LEDGER-30`…`37`) remain open for final UX/UI acceptance. Package 7B’s test snapshot overlaps the already merged PR #20 file `ToolbarGapPatternTests.cs`; it must be rebased and reviewed as an additive change, not submitted as an independent baseline. |

## Exact branch / PR / file overlap findings

### 1. Package 4A vs stale PR #66 — hard conflict

- PR #75 modifies:
  - `Desktop/ArchiveAttachmentView.cs`
  - `Desktop/Views/Archive/ArchivePage.MediaActivation.cs`
  - `Desktop/Views/Archive/ArchivePage.MediaSidebar.cs`
  - `Desktop/Views/Archive/ArchivePage.Reader.cs`
  - `Desktop/Views/Media/MediaPage.PanelKeys.cs`
  - `Desktop/Views/Media/MediaPage.TileEnter.cs`
- PR #66 modifies all six of those files, plus command/catalog, shell, gallery and test files.
- `git merge-tree --write-tree 866ad8ea2 origin/ux/ledger-16-enter-opens-gallery-20261117` reports a conflict in `Desktop/Commands/EffectiveShortcutBindings.cs`; GitHub reports PR #66 as `CONFLICTING`.

**Disposition:** #66 is not an independent merge lane. Preserve its useful design/evidence only by explicitly transferring it into the canonical Package-4 plan; do not merge #66 and #75 as separate changes.

### 2. Package 1 vs open PR #36 — semantic and exact-file adjacency

- PR #69 changes `Desktop/Views/Archive/ReaderMediaSidebar.cs`.
- Open draft PR #36 also changes `Desktop/Views/Archive/ReaderMediaSidebar.cs` as part of its broad transient-hover/popup lifetime work.
- PR #36 additionally changes `Desktop/Views/Archive/ArchivePage.Reader.cs`, which is also changed by PR #75.

**Disposition:** #69 and #36 require an owner-ordered review of the sidebar contract. A clean GitHub mergeability result for #69 does not remove the semantic overlap; retain one authoritative behavior contract and run the sidebar-focused tests after the chosen order.

### 3. Package 3 vs Package 2 — semantic dependency, no exact-file collision in the audited candidates

- PR #76 changes `libmeowchan/Tags/TagQueueItem.cs` and adds `TaggingPageMenuTests.cs` coverage for the ungrouped scope.
- Merged PR #64’s changed files are primarily `Desktop/Views/Tagging/*`, localization packs, `TaggingControlRow.cs`, `WorkspaceStyles.axaml`, and tagging layout tests; it does not change `TagQueueItem.cs`.

**Disposition:** Package 3 is safe as a narrow classifier fix, but its empty-state meaning is a dependency for any new Package-2 tagging UI work. Package 2 must consume the `null`-group contract instead of re-implementing an “empty group” interpretation.

### 4. Package 6A — duplicate evidence files

- PR #71 adds `review/package-6a-localization-findings-20261117.md`.
- PR #73 adds that same file plus `docs/diagnostics/pkg6a-localization-plurals.md`.
- Both document the same `ru`/`cs`/`pl` plural resolver finding and the same authorized-translation blocker.

**Disposition:** one canonical PR only. Retain #73 if the owner confirms the additional diagnostics file is desired; otherwise close both and open a single refreshed PR. Do not merge both: the same file path is a direct add/add collision.

### 5. Package 9 central-gate duplicate (outside Packages 1–7, but gate-relevant)

- PR #72 and PR #74 both add:
  - `review/package9/test-verification-audit-2026-11-17.md`
  - `tests/package9_audit/test_package9_inventory.py`
- Both have the same title and audit intent.

**Disposition:** this is a central-gate hygiene blocker. Keep one canonical Package-9 verification PR; do not run or report Package-9 acceptance from both branches.

### 6. Package 7B vs merged PR #20 — additive rebase required

- The committed Package-7B snapshot `b8e1a6692` changes `tests/MeowChan.DesktopTests/ToolbarGapPatternTests.cs`.
- Merged PR #20 already changed that same file as part of UX-LEDGER-01.
- The Package-7B branch is `ahead 1, behind 1` relative to the current target, so its test delta is not based on `866ad8ea2`.

**Disposition:** rebase/recreate the additive assertion from current target; do not use the old branch as a merge base or reset the analyzer/test baseline to make it fit.

## Ledger ownership and dependency map

| Ledger / contract | Current owner signal | Dependency / gate |
|---|---|---|
| Media selection, right-click, single vs double click, reader sidebar | Package 1 / PR #69; adjacent PR #36 | Resolve sidebar lifetime/selection ownership before Package 1 acceptance; rerun media and sidebar contract tests. |
| Tagging layout and empty-state semantics | Package 2 plus Package 3 / PR #76 | Package 3’s `GroupId == null` contract is the classifier source of truth; Package 2 must not duplicate it. |
| Reader/gallery command parity | Package 4 / PR #75; stale PR #66; merged PRs #41, #48, #56 | Canonical command/shortcut owner must be singular. Resolve #66 before accepting new Package-4B/4C work. |
| Booru B5–B10 | Package 5 worker lanes; no current PR | Requires per-subpackage B5…B10 status and explicit boundary against shared reader/media/shell files. |
| Localization plural/spacing/string parity | Package 6 / #73 or #71, #77 | 6A remains blocked by missing authorized translations; 6C remains audit-only until line-level inventory exists. |
| UX/UI ledger `UX-LEDGER-30`…`37` | Package 7 / PR #70 | Ledger capture is not closure. Final acceptance remains a separate review gate and must not be declared green from documentation alone. |
| Cross-package verification | Package 9 / duplicate #72/#74 | Execute only after the Package-1…7 canonical PR set is known and refreshed on the current target. |

## Required coordination actions before gate re-open

1. **Canonicalize PRs:**
   - choose one of #71/#73 for Package 6A and close/supersede the other;
   - choose one of #72/#74 for Package 9 and close/supersede the other;
   - close/supersede #66 in favor of the chosen Package-4 reader implementation, or explicitly document a cherry-pick-only transfer.
2. **Refresh retained PRs onto `866ad8ea2`** (or verify an equivalent clean merge result after the current target moves again). No force-push.
3. **Create one explicit PR for each currently unrepresented package lane:** Package 2, Package 5, Package 6B/6D, and Package 7B/7C as applicable. A clean worker worktree is not a package completion signal.
4. **Attach a changed-file manifest and ledger IDs to every package PR.** This is required for the next overlap audit; title-only package naming is insufficient.
5. **Declare merge order in the PR descriptions:** Package 3 classifier contract before any Package-2 UI interpretation; Package 4A before dependent 4B/4C shortcut work; Package 6A translation blocker stays deferred rather than papered over; Package 7 ledger capture before final UX acceptance.
6. **Run Package 9 only once against the canonical set** after the seven-package PR graph is stable. Package 9 evidence must identify the exact target head it verified.

## Gate record

| Gate | Status | Reason |
|---|---|---|
| PKG-8-01 — one canonical PR per package/ledger item | **FAIL** | Duplicate active PR pairs #71/#73 and #72/#74; stale/conflicting #66 overlaps #75. |
| PKG-8-02 — current-target branch hygiene | **FAIL** | Most active PRs use `4077eb868`; current target is `866ad8ea2`. Several current worker lanes have no delta/PR. |
| PKG-8-03 — exact-file and semantic overlap disposition | **FAIL** | Package 1/#36 and Package 4/#66/#75 require explicit owner order; Package 7B needs additive rebase. |
| PKG-8-04 — dependency ledger is explicit | **PARTIAL** | Package 3 and 6A blockers are documented; Package 2, 5, 6B/6D and 7B/7C have no canonical current PR manifests. |
| PKG-8-05 — Package 9 verification is single-source | **FAIL** | Duplicate Package-9 PRs #72/#74 remain open. |
| **Central outcome** | **BLOCKED** | Do not batch-merge Packages 1–7 until PKG-8-01…05 are rechecked and pass. |

## Reproducibility commands

```sh
git fetch --prune origin
git rev-parse origin/goal/gesamtprogramm-profile-a

git diff --name-status 866ad8ea2...origin/fix/pkg-a-media-context-contract-20261117
git diff --name-status 866ad8ea2...origin/codex/pkg4a-reader-shortcuts-20261117
git merge-tree --write-tree 866ad8ea2 origin/ux/ledger-16-enter-opens-gallery-20261117

gh pr list --repo grassmann-it/MeowChan --state open --limit 100
```

The audit deliberately records blockers instead of modifying package code, CI, baselines, or other branches to make the graph appear mergeable.

## Addendum — final worker snapshot and central preflight

**Recorded:** 2026-11-18, after all 20 worker lanes reported completion.

The earlier observations about unpublished worker state and missing current PRs are superseded by the following evidence. All 20 assigned lanes now have a clean worktree, a pushed commit, and a corresponding branch; the current PR set is still deliberately **not merged**.

### Current package PR map

| Package | Current PRs | Local evidence |
|---|---|---|
| 1 — Media | #90, #91, #97 | local focused suites green: #90 `61 passed, 2 skipped`; #91 `94 passed`; #97 `133 passed, 2 skipped` |
| 2 — Tagging | #93, #86 | #93 `46 passed`; #86 `37 passed` |
| 3 — CountFor(null) | #76, #81 | regression `1/1`; TaggingPageMenu `15/15` |
| 4 — Reader/Gallery | #87, #95, #88 | #87 `42 passed`; #95 `134 passed`; #88 `36 + 10 passed`, broader run `236 passed / 1 unrelated failure` |
| 5 — Booru | #89, #92 | #89 audit evidence; #92 `101 passed`, build `0 errors` |
| 6 — Localization | #94, #84, #80, #96 | #94 `182 + 23 passed`; #84 `42 passed`; #80 JSON/whitespace audit; #96 `34/34 passed` |
| 7 — UX/UI | #78, #82, #85 | #82 `139 passed`; #85 `12 + 100 passed`; #78 ledger/gate audit |
| 8 — Coordination | #79 | this audit |
| 9 — Verification | #83 | Python audit `4/4`, compile pass; .NET timeout documented |

### Central local preflight

The following checks were run against the 19 worker worktrees:

- every worker worktree is clean;
- `git diff --check` passes for every worker branch;
- no worker diff touches `DesktopSmoke`, `.github/workflows`, analyzer baselines, lockfiles, or restore/build infrastructure;
- every retained PR targets `goal/gesamtprogramm-profile-a` and reports GitHub merge state `CLEAN`;
- all current PRs report `statusCheckRollup: []` — no GitHub CI checks were published for these branches.

This proves branch/worktree hygiene, not product-wide acceptance. The local package reports remain the authoritative focused evidence; native GUI, platform, and central CI acceptance remain unproven.

### Remaining gate blockers

1. Duplicate/superseded PRs remain open: #71/#73 for 6A, #72/#74 for 9, and the older reader PR #66 overlaps the current reader lanes. They must be explicitly canonicalized before any merge batch.
2. Package 6A still has an authorized-translation blocker; the missing preview keys are intentionally not filled with invented text.
3. The broader localization completeness suite remains red for the two known missing preview keys; this is documented in #84/#94/#96.
4. Package 4B has one unrelated existing slideshow-key audit failure (`D` versus current `Shift+D` default); Package 4C has one unrelated gallery-help assertion failure.
5. No targeted CI retest can be called green because GitHub reports no checks for the current PRs. Workflow changes are explicitly out of scope.
6. Package-9 native/platform acceptance and DesktopSmoke evidence are unavailable from the current local environment.

**Updated central outcome:** still **BLOCKED / NOT READY TO MERGE**. The worker phase is complete; merge readiness is not.
