# UX-LEDGER-28 — package 1: section spacing and button roles

Base: `17db174fb` (`origin/goal/gesamtprogramm-profile-a` at branch creation).
Branch: `ux/ledger-28-tagging-section-spacing-20261117`.

**Implemented, but validation remains incomplete / not merge-ready.** The package's
control/header contracts pass. The normal product build is blocked by analyzer debt;
the wider unit selection retains four independently reproduced baseline failures
and one existing skip. No analyzer settings, baseline entries, skips or assertions
were relaxed to make this package pass.

The owner brief and full UX-LEDGER-28 entry, protocol rules 1.2–1.5 and 1.20,
and `ux-ui-common-errors.md` are the local acceptance references. This does not
close the whole layout audit or claim native visual acceptance.

## Changes, one implementation commit per numbered item

| Item | Before | After | Implementation |
| --- | --- | --- | --- |
| 1. Section rhythm | One header stack: every boundary 8 dip; header/list seam 8; stamp disclosure/body horizontal 16, wrapped vertical 8. Heading's shared internal spacing was 4. | Major boundaries and list seam 16 (`Workspace.Space.16`); queue progress/actions/secondary rows stay separate, grouped at 8; heading/error internals 8 (`Workspace.Space.8`); disclosure/body separation 12 on both axes (`Workspace.Space.12`). | `TaggingPage.BuildLayout`, `TaggingPage.Stamp.cs` |
| 2. Recommendation | Start, empty Add and empty Show-all permanently carried `primary`; GroupEmpty displayed both empty primaries. | Start demotes while an empty recommendation owns the surface. QueueEmpty/AllDone recommend Add; FilterNoHits/GroupEmpty recommend Show-all/Assign entries. GroupEmpty Add stays default. Start reclaims primary when rows return. | `TaggingPage.RenderEmptyState` |
| 3. Destructive role | `_emptyHow`, an accent link, changed caption and executor into Delete group. | Help remains help. A separate default/danger button owns deletion, its name, explanatory tooltip and existing confirmation executor; only a named empty group offers it. | `TaggingPage.InitializeEmptyStates`, `LocalizeEmptyStates`, `RenderEmptyState` |
| 4. Icon geometry | Cleanup buttons and selection Clear had icon content but auto width. | Page-local cleanup presentations apply `WorkspaceIcons.SquareClass` (`icon-square`, existing 32×32 input token); labelled overflow presentations remove it. Selection Clear gains only that same class. Stamp chip removal/help already used existing square families. | `TaggingPage.ApplyToolbarIcons`, `BuildSelectionBar` |
| 5. Title hierarchy | Stamp title: Section 14 + SemiBold (size AND weight). | Body 13 (`Workspace.Text.Body`) + SemiBold: weight only. | `_stampHeading` |
| 6. Stamp body | Chips, input, Add and empty hint were four peers, all 8 apart. | Current chips and add-tags group separated by 16 (including wrapping); chips and add form internals 8. Hint is on the row below the input, in its own 240-dip column, not beside Add. Input width unchanged. | `TaggingPage.BuildStamp` |
| 7. State column | Shared 12 gap + 12 State left margin = 24, in both header and rows. | No extra State margin; one shared 12 gap, same as the other columns. | `TaggingQueueRowView`, `TaggingPage.LocalizeColumns` |

`Desktop/ActionToolBar.cs`, `Desktop/Commands/CommandBindings.cs` and
`Desktop/WorkspaceStyles.axaml` are unchanged. The existing shared toolbar's 8/16
item/group layout and end docking are not implicated or modified.

## Contract evidence

`tests/MeowChan.DesktopTests/TaggingLayoutRhythmTests.cs` contains nine test cases:

- spacing is compared to the actual `Workspace.Space.*` resources, not duplicate
  numeric expectations; title offset/side margins and heading children are pinned;
- QueueEmpty, AllDone (done and skipped), FilterNoHits, GroupEmpty, non-empty,
  loading and failed presentations are exercised across all work views, in DE/EN;
- a real ready host demonstrates that an **enabled** Start still demotes for a
  filtered-empty recommendation and reclaims primary afterwards;
- help's label/role stays stable across empty states; Delete is a separate danger
  control with an executor, never the help link;
- icon-only square classes transition correctly when work-view overflow becomes
  labelled; square width/height are tied to the existing shared input token;
- title size/weight, populated/empty stamp groups, hint column/row and disclosure
  state are asserted;
- measured State/tag and tag/group gaps agree in normal/compact columns, preview
  on/off; the actual State container is retained. Non-State text cells use
  margin-preserving geometry probes to avoid requiring native text shaping.

Two existing source contracts (`PageHeaderContractTests` and
`ShellMenuAndTitleOffsetTests`) were updated for the exact new construction and
16-token bottom margin, retaining the full heading-children and title-offset
assertions. Runtime heading/offset assertions were added as well.

### Commands and actual output

All builds target only `MeowChan.csproj` or `MeowChan.DesktopTests.csproj` (and their
normal dependency graph). No solution build or GUI smoke suite was run.

Normal gate:

```text
dotnet build MeowChan.csproj --no-restore --nologo
462 Warning(s)
22 Error(s)
```

A separate untouched worktree at **the exact base** was also built with the normal
analyzer policy: 462 warnings, **21 errors**. All 21 normalized base errors recur
unchanged. The additional diagnostic is the existing ignored return value of
`KeyContextHost.Install(this, _ => "tagging")` in `TaggingPage.cs`: editing that file
invalidates its whole-file analyzer suppression. The installation call and End
ownership were not changed by this package. This is a real validation blocker,
not a green build, and no suppression was added/rebased to conceal it.

A separate test-project analyzer check (`dotnet build tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore -p:BuildProjectReferences=false --nologo`, reusing the diagnostic dependency binaries) reports **21 test-source errors on both base and head**, with no additional diagnostic in the changed test files. This check does not certify the blocked product build.

For diagnostic execution only, the affected test project was built with
`-p:RunAnalyzers=false`. This is a command-local bypass of the blocked build,
**not** a change to project/CI policy and **not** normal-gate acceptance.

```text
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-restore -p:RunAnalyzers=false \
  --filter 'FullyQualifiedName~Tagging|FullyQualifiedName~WorkspaceTokenTests|FullyQualifiedName~ButtonRoleStyleTests|FullyQualifiedName~EmptyStateStyleTests|FullyQualifiedName~SourceScanGuardTests' \
  --logger 'console;verbosity=minimal'
Failed! - Failed: 4, Passed: 248, Skipped: 1, Total: 253

dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~TaggingLayoutRhythmTests|FullyQualifiedName~PageHeaderContractTests|FullyQualifiedName~ShellMenuAndTitleOffsetTests' \
  --logger 'console;verbosity=minimal'
Passed! - Failed: 0, Passed: 53, Skipped: 0, Total: 53
```

The nine new package cases also passed alone (9/9). The 53-case run includes those
nine and both complete header-contract families; counts overlap the wider run.

The four wider failures were reproduced individually on untouched base sources
using the same diagnostic build exception:

- `MediaBooruModeTests.TheWorkViewsPutTheSwitchUnderMoreInBarebonesAndTheWaysToTaggingAlwaysThere`
  (1 failure): stale source expectation for the Media selection-row ordering.
- `TaggingSettingsTests.QIsTheDefaultKeyOfAddToTheTaggingQueueAndNoOtherCommandHasIt`
  (Windows/macOS/Linux, 3 failures): expects execution contexts `[media, gallery]`,
  while the current catalog has `[media, reader, gallery]`.

The existing skip is
`FocusClickContractTests.TaggingRightClickMustSelectAndFocusTargetBeforeComposingMenu`.
It was not added, removed or altered here.

Raw logs are in the session scratchpad (`$COMMANDCODE_SCRATCHPAD`):
`ledger28-final-build.log`, `ledger28-base-build.log`, `ledger28-focused3.log`,
`ledger28-final-tests.log`, `ledger28-clean-contracts.log`,
`ledger28-base-test.log`, `ledger28-base-shortcut-tests.log`,
`ledger28-test-analyzers.log`, `ledger28-base-test-analyzers.log`.

## Neighbouring findings and explicit follow-ups

1. **Shared default button padding is unchanged.** Evaluate the audit's proposed
   `12,4` only in a separate change with a cross-surface regression sweep.
2. **Empty ungrouped scope is misclassified.**
   `Desktop/Views/Tagging/TaggingEmptyStates.cs:21` uses `CountFor(null)` for the
   ungrouped case, but `libmeowchan/Tags/TagQueueItem.cs:176` defines that as total
   queue count. If all entries belong to named groups, empty "Without group"
   becomes FilterNoHits rather than GroupEmpty. No classifier change here; the
   primary-role test records the current classification explicitly.
3. **Analyzer debt and stale tests above remain open.** In particular, retaining
   and retiring Tagging's key-context registration is a separate lifecycle fix;
   unrelated Gallery/Archive/Media/Rules/apphost errors were not silently repaired.
4. Shared `CommandBindings.Apply(iconOnly:true)` is still not globally changed.
   This package opts this page's controls into the existing square token; a global
   adapter rollout would require its own regression scope.

## Deliberately deferred

**Package 2:** no row merges or reordering; progress remains its own row, secondary
Group/Show controls retain the existing 1100-dip breakpoint, status/footer are not
relocated, column-header styling/attachment and actual outer row heights remain
unchanged. The additional ListBoxItem padding/border is not removed here.

**Package 3:** no selection-bar grouping, primary transfer to Tag, direct
Done/Skipped/Continue actions, action order, count placement or selection-lifecycle
changes. Its Clear button received only the square class expressly requested by
item 4.

**Coverage limits:** no native screenshots/first-fixation verdict at
760/1100/1440, no light/dark visual certification, no native keyboard/pointer,
confirmation-dialog, focus-restoration, resize or platform sweep. Existing
interaction routes are unchanged; the unit selection is not native end-to-end
acceptance. Shared palette/theme tokens are reused, not re-measured. The normal
build gate and existing failures/skip keep the overall verdict incomplete.
