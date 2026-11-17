# PR #58 merge cleanup — 2026-11-17

## Integration

Merged `origin/goal/gesamtprogramm-profile-a` (first `a68099c2f`, then `17db174fb`) into `ux/ledger-26-media-sidebar-full-height-20261117` using merge commits, not rebasing.

The actual textual conflicts were:

- `Desktop/Views/Archive/ArchivePage.MediaSidebar.cs`: goal's attach/detach-managed `OnSidebarFrameLayoutUpdated` callback versus #58's intentional removal of neighbour-derived frame alignment. Kept the removal: restoring this callback would restore the height bug and reference the removed alignment method. Kept goal's independent attach/detach-managed toolbar-toggle layout subscription.
- `tests/MeowChan.DesktopTests/ReaderSidebarAlignmentTests.cs`: goal's assertions for that callback versus the new full-height contract. Kept the row-zero/row-span contract and the assertion that neighbour-derived alignment is absent.

Contrary to the anticipated conflict list, `ReaderMediaSidebarLayoutTests.cs` did not textually conflict against these goal revisions. Its exact `Presence.Direct` checks for all three work views and `OverflowMode.AsNeeded` expectation remain intact. No production overflow policy was changed. The #57 toolbar stretch change was inherited unchanged.

## Analyzer gate

The normal focused test command failed during application compilation, before executing any test. The gate exposed CA2000 in the edited sidebar file: the `KeyContextHost.Install` lease had no owner. The lease is now retained by a page-lifetime cancellation callback and disposed when `ArchivePage.EndLifetime` cancels `_lifetime`. Reparenting between reader hosts does not prematurely dispose it.

The same run also exposed debt in goal-inherited files unrelated to the full-height change, including GalleryVideoPlayer, GalleryViewer, RulesPage, GalleryDecodeQueue, GalleryTagPanelSuggestions, UpdateService, ArchivePage.axaml.cs, ArchivePage.TransferExport.cs, ArchiveTransferExportDialog and the moved apphost helpers. Their source hashes/paths no longer match the frozen baseline. No suppressions, baseline hash edits or analyzer-policy changes were made.

## Validation commands

Only the application and DesktopTests project graphs were built; no solution build or GUI smoke was run.

Normal gated command:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter 'FullyQualifiedName~ReaderMediaSidebarLayoutTests|FullyQualifiedName~ReaderSidebarAlignmentTests' \
  --logger 'console;verbosity=normal'
```

The documented warning-only capture is diagnostic evidence, not a passing analyzer gate:

```sh
dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-incremental \
  -p:DisableAnalyzerBaseline=true -p:WarningsAsErrors= \
  -p:AnalyzerBaselineCaptureDirectory="$COMMANDCODE_SCRATCHPAD/analyzer-capture"
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~ReaderMediaSidebarLayoutTests|FullyQualifiedName~ReaderSidebarAlignmentTests' \
  --logger 'console;verbosity=normal'
python3 scripts/analyzer-baseline.py "$COMMANDCODE_SCRATCHPAD/analyzer-capture"
```

A separate untouched detached worktree at `17db174fb` was used for `dotnet build MeowChan.csproj` to distinguish target-branch gate failures from this PR's changes.

## Final receipts and status

- Untouched goal application build: **failed, 21 analyzer errors**, 462 warnings; 1m41s.
- Final #58 normal gated command after fixing sidebar ownership: **failed before tests, 20 analyzer errors**. The sidebar error is gone; remaining application errors are inherited from goal.
- Documented warning-only diagnostic compilation: succeeded. For the final capture, the application and DesktopTests projects were each rebuilt with `--no-restore --no-incremental -p:BuildProjectReferences=false` and the two capture flags above, using previously built references. A fresh `analyzer-capture` directory contained just those two affected assemblies.
- Final focused component tests from those diagnostic binaries: **11 passed / 0 failed / 0 skipped**, 6.8486 seconds. This is **not** a passing normal analyzer build.
- The edited sidebar has **zero CA2000/CA2213/CA1001/CA2016 diagnostics** in the final unsuppressed SARIF. Ownership is held in `_sidebarKeyContext`, disposed and cleared on page lifetime cancellation. An initial local-only callback form still triggered CA2000; the final explicit field ownership fixed that rather than suppressing it.
- Required pruning command **refused safely**: `41 unbaselined/changed diagnostics: fix the debt, do not rebaseline it` (20 application, 21 DesktopTests). The baseline is unchanged. The complete concise location list is in the evidence directory.
- GitHub confirmed `mergeable=MERGEABLE`, `mergeStateStatus=CLEAN` after publishing the merge resolution. #58 is left open, not landed into a known-red target. This prevents mistaking absence of required GitHub checks for a successful analyzer gate.

Receipts: [`evidence/pr58-merge-cleanup-20261117/`](evidence/pr58-merge-cleanup-20261117/), including the final 11-test output, normal gate errors, pristine-goal reproduction, pruning refusal and unbaselined diagnostic locations.
