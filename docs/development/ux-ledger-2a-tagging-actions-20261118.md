# UX-Ledger Audit — Package 2A Tagging Actions

Date: 2026-11-18  
Worktree: `/home/tomka/.worktrees/ochan-ledger/worker-2a`  
Branch: `codex/package-2a-tagging-actions-20261118`  
Base: `origin/goal/gesamtprogramm-profile-a` (`866ad8ea2`)

## Audit result

The selection action bar did not cover the Package 2A ledger:

- `Done` and `Skipped` were only reachable through the state menu.
- `Continue here` was available in row menus/keys but missing from the selection bar.
- The selection toolbar did not opt into `PrioritizedOverflow`, so the policy's overflow order was not applied.
- The bar put the selection count before the actions instead of keeping actions left and count/clear trailing.
- `Remove` did not carry the destructive-action styling.

## Implemented scope

- Added direct selection actions for `MarkDone`, `MarkSkipped`, and `ContinueHere` with the existing command evaluation and execution paths.
- Kept `More status` as the complete state-menu fallback and localized the label in the central tagging catalog.
- Registered work-view IDs and policy entries for Done, Skipped, Continue, and Group.
- Made Stamp/Group/Remove the first overflow candidates while keeping Tag, decisions, and Continue required on the surface.
- Enabled prioritized overflow, preserved toolbar groups, moved actions left, and kept count plus clear in the trailing group.
- Applied the existing `danger` visual class to Remove.
- Added source/policy regression coverage for IDs, placement, overflow intent, wiring, and localization.

## Explicitly out of scope

No changes were made to DesktopSmoke, `.github/workflows`, baselines, lockfiles, build infrastructure, or the unrelated tagging right-click/FC-02 follow-up.

## Verification

Focused command:

```text
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -p:RunAnalyzers=false -p:UseSharedCompilation=false -m:1 --filter 'FullyQualifiedName~TaggingPageSelectionTests|FullyQualifiedName~TaggingPageMenuTests|FullyQualifiedName~TaggingSurfaceAuditTests|FullyQualifiedName~TaggingResponsiveLayoutTests|FullyQualifiedName~TaggingLayoutRhythmTests' --nologo -v:minimal
```

Result: **Passed — 46 passed, 0 failed, 0 skipped.**

The build emitted pre-existing compiler/Avalonia warnings; no test blocker remained. No DesktopSmoke run was performed because it is explicitly outside this package scope.
