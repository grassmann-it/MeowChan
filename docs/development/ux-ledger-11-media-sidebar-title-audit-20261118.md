# UX-LEDGER-11 — Media Sidebar/Title audit (18 November 2026)

## Scope and provenance

- Package: **1C Media Sidebar/Title** only.
- Base: `origin/goal/gesamtprogramm-profile-a` at `866ad8ea24a09d57e9e8e5e3452d4de6beaa100a`.
- Worktree: `/home/tomka/.worktrees/ochan-ledger/worker-1c`.
- Branch: `codex/worker-1c-media-sidebar-title-20261118`.
- Forbidden areas were not changed: DesktopSmoke, `.github/workflows`, analyzer baselines, lockfiles, and restore/build infrastructure.

## Concrete ledger findings audited

The open hand-over entry in `docs/development/ux-ui-release-followups-20261117.md` named:

- MD-01/PT-01: right-clicking outside a multi-selection must atomically retarget the selection before the menu is composed.
- MD-03: the Media sidebar's gallery affordance needs a hand cursor without changing reader single-click activation.
- KB-06: multi-selection shortcuts must not disappear silently when the action is unavailable.
- Selection-line fitting at the 1099/1100 boundary must remain one line and preserve the clear action's accessible name.
- The Media title/header must be in its own row so the sidebar and body layout are not offset by header chrome.

## Implemented evidence

- `Desktop/Views/Media/MediaPage.cs`: title/header is a separate `Auto,*` layout row; right-click selection uses one `SelectedItem` replacement; multi-selection menu facts and shortcut refusal reasons are explicit.
- `Desktop/Views/Media/MediaPage.Layout.cs`: body layout no longer carries the title as a child of the sidebar/grid host.
- `Desktop/Views/Media/MediaPage.Selection.cs`: counter/actions/clear share one horizontal line; width pressure moves actions under More and shortens only the visible clear label while retaining the full accessible name/tooltip; unavailable queue/delete/tag/rating paths explain themselves.
- `Desktop/Views/Media/MediaPage.FavoriteKeys.cs`: unavailable multi-selection favorite shortcuts now surface the same visible explanation.
- `Desktop/Views/Media/MediaPage.Panel.cs` and `Desktop/Views/Archive/ReaderMediaSidebar.cs`: Media opts into a gallery cursor affordance; reader/video single-click behavior remains opt-in and unchanged.
- Focused tests cover the contracts in `FocusClickContractTests`, `MediaMultiSelectionCommandTests`, `MediaPagePanelLayoutTests`, `MediaPageSelectionLayoutTests`, `MediaSelectionCounterEvidenceTests`, `MediaViewsDeadAffordanceTests`, `ReaderMediaSidebarLayoutTests`, and `ReaderMediaSidebarTests`.

## Test evidence

Command executed from the worktree:

```text
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-restore --nologo -p:RunAnalyzers=false \
  -p:UseSharedCompilation=false -m:1 \
  --filter 'FullyQualifiedName~MediaPagePanelLayoutTests|FullyQualifiedName~MediaPageSelectionLayoutTests|FullyQualifiedName~MediaSelectionCounterEvidenceTests|FullyQualifiedName~MediaViewsDeadAffordanceTests|FullyQualifiedName~MediaMultiSelectionCommandTests|FullyQualifiedName~FocusClickContractTests|FullyQualifiedName~ReaderMediaSidebarLayoutTests|FullyQualifiedName~ReaderMediaSidebarTests' \
  --logger 'console;verbosity=minimal'
```

Result: **Passed 133, Skipped 2, Failed 0, Total 135**. The two skips are the pre-existing native focus-click contract skips; no DesktopSmoke or native GUI run was claimed.

The test invocation used `RunAnalyzers=false` only as a command-line setting to avoid unrelated analyzer noise; no analyzer baseline was changed. Restore was performed earlier in locked mode only to materialize missing local assets; no lockfile or build-infrastructure file is part of this audit.

## Remaining release gate

The code-level Package 1C contracts are green in this worktree. The ledger item remains **native-validation pending** until the Ubuntu/native GUI sweep records the sidebar/title offset, hand cursor, right-click selection, and 1099/1100 visual evidence. That is an evidence gate, not a focused-test blocker.
