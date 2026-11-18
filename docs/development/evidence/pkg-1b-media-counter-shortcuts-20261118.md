# Package 1B — Media counter / shortcut audit (2026-11-18)

## Scope and baseline

- Worktree: `/home/tomka/.worktrees/ochan-ledger/worker-1b`
- Branch: `worker/1b-media-counter-shortcuts-20261118`
- Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea24a0`
- Package scope: media counter evidence and media selection shortcuts only.

The existing counter implementation and its evidence tests were audited. No counter layout or formatting change was justified by the current ledger: the existing dynamic digit reservation, selection-bar sizing, and counter evidence coverage remain unchanged.

## Implemented, evidence-backed fixes

1. **No silent multi-selection shortcut consumption.** Batch actions that cannot run because their host/action is unavailable now surface the existing selection feedback instead of returning silently. This covers gallery open, original export, tag actions, and the routed multi-selection shortcut path.
2. **Rating shortcuts keep frozen target identities.** A multi-selection rating shortcut now snapshots eligible saved/rating-known rows in page order and continues after the first write passively replaces the grid rows. It no longer cancels the remaining targets merely because the items source was refreshed. The busy state is restored in `finally`.
3. **Regression coverage.** Added tests for passive row replacement during a rating shortcut and for missing-host feedback on details/export/rating shortcuts.

No `DesktopSmoke`, `.github/workflows`, analyzer baselines, lockfiles, or restore/build infrastructure were changed.

## Focused verification

- `dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -p:RunAnalyzers=false -p:UseSharedCompilation=false -m:1 -p:BuildProjectReferences=true --filter 'FullyQualifiedName~MediaMultiSelectionCommandTests|FullyQualifiedName~MediaSelectionCounterEvidenceTests|FullyQualifiedName~MediaPageSelectionLayoutTests|FullyQualifiedName~MediaSingleKeyShortcutTests|FullyQualifiedName~MediaPageRoutedShortcutRegressionTests|FullyQualifiedName~MediaShortcutRoutingTests' --logger 'console;verbosity=minimal'`
  - **94 passed, 0 failed, 0 skipped**.
- `dotnet build MeowChan.csproj --no-restore --nologo`
  - **0 errors, 470 warnings**, completed successfully in 5:07.
- `git diff --check`
  - clean.

## Risks and remaining gate warning

- This is deterministic unit/behavior coverage; it does not replace native Windows/macOS key delivery or a full GUI smoke run.
- The normal build is green, but the analyzer output still contains the repository's pre-existing warning debt (including media-page and X11 interop/performance warnings). No analyzer baseline or suppression was changed to make this package pass.
- The focused test command intentionally uses the command-local `RunAnalyzers=false` switch so the package behavior can be verified independently of that warning debt.
