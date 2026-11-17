# UX-LEDGER-25 — Archive results row occupancy

## Contract and history

The latest owner decision supersedes UX-LEDGER-01's compact Archive layout pair:
fill unused horizontal space, but keep the pair last, joined, and always on the bar.
Groups 1/2 stay at the start; Columns stays a natural-width Group 3 End action
before the pair and may overflow independently.

Verified history (the reported attribution was slightly imprecise):

- `0bfb79bcd` added `HorizontalAlignment="Stretch"` to the existing Stretch pair,
  with `OverflowMode="AsNeeded"`.
- `d5e175155` actually removed Stretch, moved the pair after the primary actions,
  and introduced Group 3 / End.
- `b7549cefb` retained that order/End and changed only the two overflow modes to
  Never. Its keep-visible fix must remain intact.

## Implementation semantics

- **Group** affects both measurement (16 DIP between different visible groups,
  8 between ordinary neighbours, zero between joined segments) and arrangement
  (the same gaps plus separator marks). It is not a container or stretch weight.
- **End** does not alter measurement or overflow eligibility. Arrangement shifts
  the trailing contiguous End run by the unused width, keeping natural widths.
  A stretching trailing segment run takes precedence over that shift.
- **Stretch** does not alter natural measurement or overflow decisions. It divides
  spare arrange space among the trailing contiguous opted-in segments of one group,
  equalizing visible widths where natural minima permit. HorizontalAlignment Stretch
  lets each control use its assigned slot. Previously it required nonempty overflow
  and used a conservative measurement budget; the corrected rule uses the actual
  presenter slot, whether or not More is shown. Ursa already reserves More's slot
  and leading margin. No extra gap may be reserved a second time.

Restoring Stretch while retaining End, order, Group 3 and Never preserves both
full-width occupancy and the keep-visible guarantee from `b7549cefb`.

## Deterministic verification

`ActionToolBarRenderedStretchTests.ArchiveResultRowsFillTheSameContentEdgeAndNeverOverflowTheLayoutPair`
uses the installed Semi/Ursa template and actual Avalonia measure/arrange. It reads
production item order and attached layout properties from ArchivePage.axaml, with
fixed natural action widths (70 DIP) and 64-DIP segment minima to remove font and
locale variability. Search/filter geometry stand-ins share the archive's Grid,
ScrollViewer and margins; this is not full Archive page startup or native GUI acceptance.

The fixture tests 180, 330, 600 and 1000 DIP, both initial Columns visibility states,
visibility round trips, stable repeated layout, primary start placement, Columns'
natural width and gap, joined segments, direct-host membership (not overflow),
row/content/More right edges and absence of unused internal or trailing space.
Used occupancy includes the normal item/group/More gaps, but excludes gratuitous
End-shift gaps. Merely checking the rightmost edge would miss this regression:
End already docked the compact pair to the correct right edge.

### Measured occupancy (DIP)

The identical final fixture was run with the base production files restored and
rebuilt, then with the fix. Before, all eight new cases fail on unused space;
after, all eight pass, including their visibility round trips.

| Container | Before, Columns hidden | Before, Columns visible | After, either state | After segment widths, hidden / visible |
| ---: | ---: | ---: | ---: | --- |
| 180 | 152 | 152 | 180 | 78 each / 78 each |
| 330 | 316 | 316 | 330 | 71 each / 71 each |
| 600 | 558 | 558 | 600 | 85 each / 85 each |
| 1000 | 612 | 690 | 1000 | 258 each / 219 each |

The installed test template measures More at 16 DIP plus its normal 8-DIP leading
margin when needed. At 1000, More is absent and Columns is on-bar when visible;
at the narrower widths Columns moves under More independently. Both toggles
remain in the direct host at every width, never in overflow. After the fix the
right edge matches search/filter (or More matches it, with exactly the normal
8-DIP gap before More), and unused internal/leading/trailing space is zero.

### Validation receipts

Affected project build and seven focused test classes: **258 passed, 0 failed**.

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore \
  --filter 'FullyQualifiedName~ActionToolBarRenderedStretchTests|FullyQualifiedName~ArchiveSegmentStretchTests|FullyQualifiedName~ActionOverflowLayoutTests|FullyQualifiedName~ToolbarGapPatternTests|FullyQualifiedName~ToolbarUpdateTests|FullyQualifiedName~MediaSortToolbarContractTests|FullyQualifiedName~GalleryToolbarFitTests'
```

A broader toolbar-name diagnostic selected 309 tests: 304 passed, five unrelated
reader-contract failures. All five were reproduced against the unchanged base
production files (21 selected: eight existing passes, five existing failures,
eight expected new regression-test failures). No assertions were removed or
relaxed to hide those failures:

- `ReaderWorkViewTests.ToolbarKeepVisibleOrderIsTheOverflowOrderAndEndsWithTheRarelyUsedActions`
- `ReaderMediaSidebarLayoutTests.DefaultToolbarStateMatchesTheDefaultScopeWithoutChangingPreferences`
- `ReaderToolbarRl1Tests.TheGearIsTheViewsOldRowAndFullScreenEndsTheOrderAndOverflowsOnlyWhenItDoesNotFit`
- `ReaderToolbarRl1Tests.TheBarStandsInItsGroupsInTheOrderOfTheSpec`
- `ReaderToolbarRl1Tests.LargeReaderCarriesTheOpenElsewhereIconAndTheListsReadKeepsTheEye`

Session evidence: `ledger25-base-confirm.log` (unchanged base measurements and
failure receipts), `ledger25-verified.log` (broader diagnostic), `ledger25-final.log`
(final 258 passes), all under `$COMMANDCODE_SCRATCHPAD`.

Coverage limits: no full solution build, GUI smoke suite, native screenshots,
platform input, contrast/theme matrix or unrelated surface certification. Overall
repository tests are not claimed green; the affected layout/overflow checks are green.
