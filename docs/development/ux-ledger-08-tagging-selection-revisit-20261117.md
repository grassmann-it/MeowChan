# UX-LEDGER-08 — tagging selection revisit regression

## Outcome and base correction

The requested target, `origin/goal/gesamtprogramm-profile-a`, resolved to
`f222af3ab` when this worktree was created. Contrary to the delegated task's
ancestry premise, `548b25ae37cdd566ad4b3aa881004b32b8060c2c`
(**Fix tagging selection count after page refresh**) is already an ancestor
of **both** that target and the reported original HEAD, `3db8f9701`.
Both of these commands exit 0 in this checkout:

```sh
git merge-base --is-ancestor 548b25ae3 f222af3ab
git merge-base --is-ancestor 548b25ae3 3db8f9701
```

The existing native-selection reset is present unchanged in
`Desktop/Views/Tagging/TaggingPage.cs`, in `Render`. The reported increasing
count did not reproduce on this base in real shell navigation. This delivery
therefore adds executable lifecycle coverage, not a second speculative product
fix or a count-only workaround. A different installed binary/profile may still
need investigation if the report persists after deploying this goal.

## Root cause and retained fix

`MainWindow.ActivateTaggingPage` calls `ActivateAsync` on every visit.
`ActivateAsync` loads the queue and calls `Show`, which calls `Render` even
when no queue entries changed. `TaggingQueueView.Rows` creates replacement row
objects. Before 548b25ae3, Render rebound `ItemsSource` and then appended the
carried selection by key; a retained native selected row could coexist with
its replacement and inflate `SelectedItems.Count` on each visit.

548b25ae3 already clears **both** ListBoxes' `SelectedItems` and `SelectedItem`
under `_rendering` before rebinding. It then restores only surviving keys,
lead first, to the new active rows. Only the active view is bound. The count
and commands still read actual native selection; no distinct-key count masks
stale objects. Filter/group/scope narrowing drops hidden selections, and
widening does not resurrect them. The existing initial keyboard-focus/default
row selection behavior is intentionally unchanged.

Inspection covered `ActivateAsync` / `Show` / `Refresh` / `Render` /
`SelectedKeys`, selection count/bar/clear, list/grid switch and selection
events, group and state-filter handlers, effective scope, and work-view density.
No Reader-Media-Activation, Gallery-Context-Core, or plugin-signature code was
changed.

## New deterministic regression

`tests/MeowChan.DesktopSmoke/TaggingPageChecks.cs` now checks, against the real
main window, host, and a disposable three-entry queue:

- Twenty navigate-away/back cycles: five each for single/multiple selection
  in list/grid mode, using non-default rows so focus cannot hide a lost selection.
- Exact selected keys, current row **reference identity**, native collection
  length, selection-model index count, lead, label/bar, and realized containers.
- List/grid round trips and an unbound, unselected hidden view.
- All work views, narrowed/widened scope, named/ungrouped/all groups,
  empty/reset state filters, and explicit clear.
- Separate list/grid captures after the multi-selection revisit cycles.

The existing selection source-contract tests remain in place. The previous
smoke's Delete/reorder, menus, stamp, state changes, saved view, clear queue,
and theme/language checks still run after the new regression.

## Validation

- Desktop tagging tests: **172 passed, 1 skipped, 0 failed** (173 total).
- Selection contracts alone: **7 passed, 0 failed**.
- Expanded real-window tagging smoke on Linux/Xvfb: **passed**, including all
  twenty revisit cycles and view/scope/filter/group checks; no binding errors.
- Builds succeeded with existing repository warnings (including AVA3001 and
  unrelated nullable/obsolete API warnings).
- An optional mutation build removing the inherited reset exceeded the
  ten-minute build timeout under concurrent compiler load, before the smoke
  started. The mutation was restored byte-for-byte; no negative-control result
  is claimed.

Reproduction commands (from the worktree; use an owned temporary directory):

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter 'FullyQualifiedName~Tagging' --nologo -v:q
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj \
  --nologo -v:q
TMPDIR="$COMMANDCODE_SCRATCHPAD" \
  MEOWCHAN_TAGGING_PAGE_ONLY=1 MEOWCHAN_DESKTOP_SMOKE_XVFB=1 \
  xvfb-run -a -s '-screen 0 1920x1080x24' \
  dotnet tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll
```

Evidence logs are session-local under `$COMMANDCODE_SCRATCHPAD`:
`ledger08-tests.log`, `ledger08-expanded.log`, `ledger08-final-build.log`,
and `ledger08-final-smoke.log`. Captures are emitted into the smoke build
output, including `smoke-tagging-revisit-list.png` and
`smoke-tagging-revisit-grid.png`. This is Linux managed-input evidence, not
native-pointer or Windows/macOS acceptance.

## Revisit delivery after PRs #22 and #24 (2026-11-17)

Rebased the contract commit onto `afc35bc4e2a18cbed8cd34f42dbd699e7afda3d6`
(`origin/goal/gesamtprogramm-profile-a`, including #22 and #24). Then merged the
previously published contract tip `24ffecb10be3877dff4aa1ace7966d43ad6c78b7`
back into the rebased history without content changes, so updating PR #23 is a
normal fast-forward push: no force-push and no published work discarded.

The new assignment's premise that the product fix is missing is contradicted by
the repository: `git merge-base --is-ancestor 548b25ae3
origin/goal/gesamtprogramm-profile-a` exits 0. Moreover,
`Desktop/Views/Tagging/TaggingPage.cs` is byte-for-byte identical to that file in
548b25ae3 (SHA-256
`37389a0d871f730ebfe091bb58c170e0ef7019c412834f080d8ccb0a46a89c9c`).
The clear-before-bind root-cause fix is already present; no duplicate or
speculative product change was introduced. No tests or assertions were changed,
and no timeout was raised. The unchanged pinned smoke passes on the rebased base.

Fresh validation on the integrated tree (`9d9e01d4a`):

```text
$ dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
    47 Warning(s)
    0 Error(s)
Time Elapsed 00:05:12.20

$ dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --filter 'FullyQualifiedName~Tagging' --nologo -v:q
Passed!  - Failed:     0, Passed:   172, Skipped:     1, Total:   173, Duration: 2 m 35 s - MeowChan.DesktopTests.dll (net10.0)

$ MEOWCHAN_TAGGING_PAGE_ONLY=1 TMPDIR="$COMMANDCODE_SCRATCHPAD" python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode normal --output "$COMMANDCODE_SCRATCHPAD/ledger08-baseline"
Desktop harness normal: passed (exit 0)
SMOKE tagging-selection-lifecycle: 20 revisit cycles; single/multi selection, lead, native model, current row identity, containers, list/grid, work views, scope, groups, filter and clear passed.
SMOKE tagging-page: footer button and More entry, progress, Delete/Alt+Down, context menu, selection bar, grid view and switch, stamp, filter, work views, clear; Light/Dark EN/DE captures.
SMOKE SCENARIO: normal+MEOWCHAN_TAGGING_PAGE_ONLY
```

The runner reports `passed: true`, `exit_code: 0`, `timed_out: false`, actual
render scaling 1, no binding errors, and normal shutdown/profile-lease release.
It ran in private Xvfb/Openbox/DBus on Linux, not the personal desktop. Both list
and grid revisit captures were emitted into the Release smoke output directory.
Full session-local evidence: `ledger08-build.log`, `ledger08-revisit-tests.log`,
`ledger08-baseline-run.log`, `ledger08-baseline/results.json` and
`ledger08-baseline/normal/output.log` under `$COMMANDCODE_SCRATCHPAD`.

UX-LEDGER-08's targeted merge gate is green. This is not a claim that unrun
repository-wide CI or Windows/macOS acceptance has passed. At inspection time
PR #23 exposed no remote status checks.
