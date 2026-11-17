# UX-LEDGER-26 — reader media sidebar spends its host height on media

Implemented against `origin/goal/gesamtprogramm-profile-a` at `1dbd86c84`.
Implementation: `bfb02cd8d`; existing reader smoke extension: `dd1de1023`.
Contract: final-review protocol **1.9e**, superseding the older 16 November
frame-to-posts-edge alignment. Error class: **UX-E17**, with UX-E12 layout ownership.

## Cause and change

There was no sidebar `MaxHeight` and no media `ScrollViewer` height cap.
`ArchiveRoot`'s `Auto,*,Auto` rows and the spanning `ArchiveSplit` already give
the reader the remaining page height. The cap was inside that allocation:

* Embedded hosting put the sidebar in **row 1 only**, beside the reader box,
  with a negative title-band margin instead of allocating the entire column.
* In **both** hosts, `sidebar.LayoutUpdated` called `AlignMediaSidebarFrame`.
  `ReaderSidebarAlignment.InsetsFor` computed vertical padding from the
  neighbouring reader box: top `max(0, boxTop - sidebarTop - headerBand)` and
  bottom `max(0, sidebarBottom - boxBottom)`. Thus a short neighbour literally
  converted the remaining media height into bottom padding. The outer sidebar
  could already fill its host in the large reader while its media frame did not.

The sidebar and splitter now span both embedded-reader rows (row 0, span 2);
the detached frame uses row 0, span 1. Removed the neighbour-derived alignment
callback/arithmetic, negative lift and vertical padding. Horizontal host insets,
side columns, width persistence, responsive collapse and reparenting are unchanged.
The existing fixed 32-DIP title and 8-DIP gap remain. `ReaderMediaSidebar`'s
existing star media row / natural-height chrome rows absorb growth without a
fixed pane/media height.

Obsolete tests of the removed alignment formula were replaced by measured real
control-tree assertions in **the existing `ReaderMediaSidebarLayoutTests`**.
The remaining alignment wiring guard pins the new span and stable title contract.

## Measurements (DIP)

Component fixtures arrange the actual `ArchiveRoot` or detached frame at the
listed geometry, without an OS window. Embedded measurements exclude the page's
natural header/margins. Sidebar minimum-reader-width preference is set to its
supported minimum so these probes exercise the visible sidebar, not collapse.
Both sides give identical heights. No strip/transport in this first table:

| Geometry | Embedded host | Embedded pane before → after | Embedded media before → after | Large host/pane before = after | Large media before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1440 × 720 | 654 | 644 → 654 | 604 → 614 | 720 | 670 → 680 |
| 1440 × 900 | 834 | 824 → 834 | 784 → 794 | 900 | 850 → 860 |
| 1920 × 1080 | 1014 | 1004 → 1014 | 964 → 974 | 1080 | 1030 → 1040 |

**Negative control reproducing the growing empty strip:** top-align a short
neighbour (its measured box is 49 DIP in this fixture). Before the fix the media
frame stays **49 → 49 → 49** in both hosts as the window grows. Embedded bottom
padding grows **555 → 735 → 915**; large-reader bottom padding grows
**621 → 801 → 981**. After the fix, changing between stretching and short
neighbours leaves the media heights in the table unchanged, with zero vertical
padding. These are deliberately adversarial component measurements, not claims
about the user's original screenshot dimensions.

The regression asserts media-frame **area / total sidebar area**, not just height:

| Host | No extra chrome, growing geometry | With strip + natural transport-content row |
| --- | --- | --- |
| Embedded, either side | .938838 → .952038 → .960552 | .876147 → .902878 → .920118 |
| Large reader, either side | .879514 → .889861 → .896759 | .826484 → .847437 → .861406 |

Chrome height stays constant within each growth sweep; every DIP of additional
host height goes to the media frame. The lower large-reader area ratio includes
its preserved horizontal insets. The media frame is the allocated image/video
viewport, not the aspect-ratio-dependent painted bitmap (letterboxing is allowed).

Real Linux/X11 window smoke, 1600 × 960 client: **large host/pane 912/912, media
872** and **embedded host/pane 757/757, media 717**, on both left and right.
Four screenshots emitted as `smoke-reader-full-height-{host}-{column}.png`.

## Previously failing toolbar assertion

**Outdated expectation, not a product regression.** Commit `071e43347`
("Register reader media sidebar in work-view policy") explicitly made this a
`Direct` tool in Barebones/Simple/Advanced and placed it in the toolbar keep-visible
order. `WorkViewPresenter.ToolBarMode` deliberately maps `Direct` to `AsNeeded`;
only `Required` maps to `Never`. The XAML's initial `Never` is overwritten by that
policy. Updated the exact assertion to `AsNeeded` and additionally assert all
three policy entries are `Direct`; no permissive either/or assertion and no
production overflow-policy change.

## Validation and limits

Affected app build and focused DesktopTests: **11/11 passed**. Includes stable title
across tall/wide/square images, serialized preferences, shared close/shortcut toggle,
both host/side combinations, three geometries, short-neighbour independence, area
ratio and natural chrome invariants, responsive collapse/space return, and restored
configured sidebar width. No full solution build or full smoke suite.

```sh
dotnet build MeowChan.csproj --no-restore -t:Rebuild -p:BuildProjectReferences=false
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj --no-restore \
  -p:BuildProjectReferences=false \
  --filter 'FullyQualifiedName~ReaderMediaSidebarLayoutTests|FullyQualifiedName~ReaderSidebarAlignmentTests' \
  --logger 'console;verbosity=detailed'
dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj --no-restore \
  -p:BuildProjectReferences=false
TMPDIR="$COMMANDCODE_SCRATCHPAD" python3 scripts/run-desktop-smoke.py \
  --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll \
  --output "$COMMANDCODE_SCRATCHPAD/ledger26-smoke-v2" \
  --registry --only reader-media-sidebar --timeout 240
```

One reader selector: **passed** after replacing its stale `Grid.Row == 1` assertion
with row-0/span-2 plus actual full-height/top/bottom/title/padding assertions.
First attempt artifacts are retained: the runner's first launch hit its known
Openbox-startup failure, its automatic infrastructure retry reached the stale-row
assertion and failed. The corrected-build run passed; it is not a blind retry of
unchanged failing code. Initial overlapping/incremental builds also produced a
missing-precompiled-XAML fixture failure; a serial affected-app rebuild restored
the valid artifact before measurements. No assertion was disabled.

Session evidence root: `$COMMANDCODE_SCRATCHPAD`:
`ledger26-baseline-heights.log`, `ledger26-baseline-short-posts.log`,
`ledger26-after.log`, `ledger26-build.log`, `ledger26-smoke/registry.json`,
`ledger26-smoke-v2/registry.json`, and
`ledger26-smoke-v2/reader-media-sidebar/reader-ux/` (output log, capture metadata,
four new geometry screenshots and existing shortcut capture).

Coverage classification for the UX checklist: E11/E12/E17 geometry and E28 stale
expectation exercised by component tests; E03–E05/E08/E09/E13/E20/E22 related
reader routes/state have only the existing selector's scoped managed-input and
window evidence, not new native certification. E01/E02/E06/E07/E10/E14–E16/E18/E19/
E21/E23/E24 remain outside this focused layout change (no new input, decoding,
playback, error, sorting or accessibility implementation). E25–E27: private fixture,
input labels and all attempt receipts retained; no full-product acceptance claim.
Windows/macOS, native splitter dragging, native IME, full theme/locale/scale matrix,
video playback continuity and independent Astra/Fable visual review are **unverified**
in this task. No other ledger finding is claimed closed by these focused greens.
