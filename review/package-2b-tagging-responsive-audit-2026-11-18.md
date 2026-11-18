# Package 2B — Tagging Responsive Selection-Bar Audit (2026-11-18)

## Scope

This audit is limited to the Tagging page selection bar and its responsive overflow/keyboard path:

- `Desktop/Views/Tagging/TaggingPage.Selection.cs`
- `Desktop/Views/Tagging/TaggingPage.Commands.cs`
- `Desktop/Views/Tagging/TaggingPage.WorkView.cs`
- `Desktop/WorkViews/WorkViewPolicy.Tagging.cs`
- `Desktop/ActionToolBar.cs`
- focused `MeowChan.DesktopTests` only; `DesktopSmoke` is out of scope

Worktree: `/home/tomka/.worktrees/ochan-ledger/worker-2b`  \
Branch: `codex/worker-2b-tagging-responsive-20261118`  \
Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea2`

## Findings

1. **Selection-bar geometry is already explicit and measured.** The bar uses `Auto,*,Auto`: the live selection count is outside the action toolbar, the measured action toolbar owns the middle column, and Clear is a separate trailing control. The action toolbar refreshes its overflow label after selection state changes.
2. **Core actions are protected from responsive overflow.** Work-view policy marks Tag, Remove and Clear as `Required` in every work view. Stamp and State use the policy's reduced placements; the toolbar's `More` path remains available at narrow widths.
3. **The inherited overflow implementation already covers the reported failure modes.** `ActionToolBarPanel` accounts for measured `More` width, keeps joined segments together, invalidates after overflow-mode changes, repairs focus when an action moves between the direct host and overflow host, and handles Escape/Home/End/Up/Down in the overflow popup.
4. **Selection Escape is handled before tagging command matching.** The list tunnel consumes plain Escape when there is a selection, clears the native selection, and only then allows the catalog-command loop to run for other keys.
5. **The previously reported Tagging regressions are already fixed on this base.** The base contains the queue-table compact-width fix (`483ca71d6`), stale native-selection reset (`548b25ae3`), and the movable state-menu origin repair (`ec0beeeed`). No second speculative production fix is justified without a failing reproduction.

## Delivery

Added `tests/MeowChan.DesktopTests/TaggingResponsiveSelectionBarTests.cs` with focused contracts for:

- measured selection-bar geometry and placement of count/actions/Clear;
- required-vs-reduced selection actions plus a representative narrow-width overflow decision;
- list Escape precedence and overflow-popup keyboard navigation/return-to-More behavior.

No `DesktopSmoke`, `.github/workflows`, analyzer baselines, lockfiles, Restore/Build-Infra, or unrelated desktop surfaces were changed.

## Validation

Run the focused test filter from this worktree after restore:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-restore \
  --filter 'FullyQualifiedName~TaggingResponsiveSelectionBarTests|FullyQualifiedName~TaggingResponsiveLayoutTests|FullyQualifiedName~TaggingPageSelectionTests|FullyQualifiedName~TaggingSurfaceAuditTests|FullyQualifiedName~TaggingRowStructureTests' \
  --logger 'console;verbosity=minimal'
```

Result: **Passed — 37 passed, 0 failed, 0 skipped** (58 s) after the locked restore. The run used the focused filter above with `-m:1`, `-p:BuildInParallel=false`, `-p:RunAnalyzers=false`, and `-p:UseSharedCompilation=false` to avoid unrelated compiler contention. Existing repository nullable/legacy warnings were emitted; no analyzer baseline was changed. No native smoke claim is made.
