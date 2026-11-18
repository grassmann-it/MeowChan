# UX-LEDGER-17 — shortcut collisions

Implemented the supplied ranked proposal on `ux/ledger-17-shortcut-collisions-20261117`, based on `409975af09e9b21c704c880bf373b81c6829ab4b` (the current `origin/goal/gesamtprogramm-profile-a` when work began). No replacement gestures were re-derived.

## Binding families and pushed commits

`Primary` means Ctrl on Windows/Linux and Command on macOS. `Back` is Avalonia's Backspace key.

| Order | Command(s) moved | Before | After | Pushed commit |
| --- | --- | --- | --- | --- |
| 1 | `tags.queue.skip` | Space | S | `4a46c7473` |
| 2 | `video.speed-reset` | Back | Shift+Back | `25f1309a0` |
| 3 | `video.snapshot` | S | Shift+S | `6b56bb9ed` |
| 4 | `gallery.slideshow-toggle` | D | Shift+D | `c4812f886` |
| 5a | `tags.queue.done-next` | Enter | Primary+Enter | `d17db4cee` |
| 5b | `reader.expand` | Primary+Enter | Primary+Alt+Enter | `7860d65d5` |
| 6 | `video.volume-up` / `video.volume-down` | Up / Down | Shift+Up / Shift+Down | `f5e159551` |
| 7 | `video.seek-back` / `video.seek-forward` | Primary+Left / Primary+Right | Alt+Left / Alt+Right | `ff545b3d6` |

Each family was committed and pushed independently, in this order. The two Enter meanings were split into separate commits, giving eight binding commits for the seven numbered proposal steps. A final follow-up synchronizes help, exact ownership assertions and remaining localization-test fixtures.

### Deliberately unchanged

- `video.play-pause`: Space.
- `tags.queue.back`: Back.
- `tagging.mark-skipped`: S, deliberately shared with `tags.queue.skip` in the other tagging surface.
- `tagging.mark-done`: D.
- `tags.quick.commit-next`: Primary+Enter, deliberately shared with `tags.queue.done-next` for commit-and-advance.
- All four Enter open/activate commands: `tagging.continue-here`, `thread.read`, `media.open`, `reader.media.activate`. No UX-LEDGER-16 activation handlers were changed.
- Reader post navigation: Up / Down.
- macOS back navigation (`media.return`, `reader.history-back`, `gallery.return`): Primary+Left.
- Far seeks: Primary+Shift+Left / Primary+Shift+Right.
- Reader sidebar media navigation: Alt+Shift+Left / Alt+Shift+Right.

No requested move was left unimplemented. Existing explicit user overrides remain user-owned; this changes defaults, not saved profiles.

## Runtime files

Every binding family changes `Desktop/Commands/CommandCatalog.cs` and its revision entry in `Desktop/Commands/EffectiveShortcutBindings.cs` (defaults revision 22).

Additional files:

- `Desktop/Commands/GalleryShortcutScheme.cs`: snapshot and slideshow are explicit shifted-action exceptions, not plain playback letters.
- `Desktop/GalleryViewer.TaggingMode.cs`: comments updated to match the new defaults; routing implementation unchanged.
- `docs/Help.md`: current shortcut table, tagging-walk instructions, gallery description and snapshot guidance updated. Historical release entries are not rewritten.

### Non-text Enter exception

The requested Primary+Alt+Enter would otherwise be rejected by the blanket Ctrl+Alt/AltGr validator on Windows/Linux. Validation now permits exactly Ctrl+Alt+Enter (no Shift or additional modifier); every other Ctrl+Alt key remains rejected. Existing invariant tests now pin that sole exact default owner/chord, and a new test iterates every other Avalonia key to verify AltGr protection remains in place. Explicit reader bindings on the new chord are also tested, not just catalog defaults.

### Localization

No localization entry embeds one of the changed gesture literals: menu/tooltips/keycaps and the tagging announcement obtain effective binding displays dynamically. The tagging announcement already uses `{0}`, `{1}`, `{2}` in English, German and all ten bundled packs, so no pack translation edits are necessary. Existing twelve-language scheme/tagging/seek tests were retained. The two localization warning fixtures formerly manufacturing a yield with Up now use Shift+Up; their warning, state and accessibility assertions are unchanged.

## Tests extended (existing suite only)

All paths below are under `tests/MeowChan.DesktopTests/`.

| Family | Test files changed or extended |
| --- | --- |
| All moved commands, intended sharing and unchanged navigation | `DefaultShortcutInvariantTests.cs` |
| Space / queue completion | `TaggingModeKeyRouteTests.cs`, `TaggingDecisionLogTests.cs` |
| Speed reset / short seeks / snapshot / slideshow | `ViewerQuickWinsTests.cs` |
| Snapshot / slideshow exceptions | `GalleryShortcutSchemeTests.cs`, `HelpDocumentationTests.cs` |
| Slideshow | `MediaSingleKeyShortcutTests.cs`, `Release102PlaybackTests.cs` |
| Reader expansion | `DefaultsRevision19Tests.cs`, `ShortcutBindingsTests.cs` |
| Volume | `AdditiveGalleryShortcutTests.cs`, `MediaInteractionExtraRegressionTests.cs`, `GalleryVolumeShortcutContextBoundaryTests.cs`, `GallerySeekNavigationPolicyTests.cs`, `GalleryPlaybackContinuityRegressionTests.cs`, `ShortcutIssueLocalizationTests.cs` |
| Short seeks | `VideoKeyboardTests.cs` |

For every moved command, tests resolve the new gesture and reject its old gesture on Windows, Linux and macOS. Speed reset is checked in gallery, reader and Media contexts. Intended sharing is checked in each tagging context and by exact global owner sets. Far seeks, reader sidebar arrows, post navigation, Mac back navigation and the four Enter activation defaults are pinned separately. Native editor, transport ownership, handled-event and shortcut-capture guards remain asserted.

## Validation

Only `tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj` and its referenced production build graph were built/tested. No solution build and no GUI smoke suite were run. Subsequent builds used `-p:MeowChanPluginDeliveryEnabled=false`; final fast iterations also used `-p:RunAnalyzers=false` (no test assertion disabled). Initial analyzer-enabled builds emitted existing compiler/analyzer warnings.

Per-family focused test output (overlapping selections, not additive):

| Selection | Failed | Passed | Skipped |
| --- | ---: | ---: | ---: |
| Urgent Space/Back tagging checks | 0 | 21 | 0 |
| Snapshot/scheme/default invariants | 0 | 74 | 0 |
| Slideshow/scheme | 0 | 67 | 0 |
| Queue Enter and intended sharing | 0 | 27 | 0 |
| Reader expansion/defaults/editor | 0 | 135 | 0 |
| Shifted volume/transport boundaries | 0 | 61 | 0 |
| Short seeks/Mac/sidebar/video | 0 | 237 | 0 |
| Final affected shortcut selection | 0 | 481 | 0 |
| Localization warning fixtures | 0 | 12 | 0 |

Two earlier analyzer-enabled invocations exceeded the ten-minute tool deadline after compilation; their no-build retries passed (74 and 135 tests respectively).

Final extended regression output:

```text
Failed!  - Failed: 2, Passed: 1053, Skipped: 0, Total: 1055,
Duration: 10 m 12 s - MeowChan.DesktopTests.dll (net10.0)
```

The only remaining failures are the two unchanged baseline assertion mismatches documented below. All changed shortcut/localization expectations pass.

The extended selection is the existing tests whose fully-qualified names match `Shortcut`, `DefaultsRevision`, `YieldingDefaults`, `VideoKeyboard`, `ViewerQuickWins`, `TaggingModeKeyRoute`, `TaggingDecisionLog`, `GallerySeekNavigationPolicy`, `GalleryPlaybackContinuityRegression`, `MediaInteractionExtraRegression`, `HelpDocumentation`, or `Release102Playback`. It is run using:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName~Shortcut|FullyQualifiedName~DefaultsRevision|FullyQualifiedName~YieldingDefaults|FullyQualifiedName~VideoKeyboard|FullyQualifiedName~ViewerQuickWins|FullyQualifiedName~TaggingModeKeyRoute|FullyQualifiedName~TaggingDecisionLog|FullyQualifiedName~GallerySeekNavigationPolicy|FullyQualifiedName~GalleryPlaybackContinuityRegression|FullyQualifiedName~MediaInteractionExtraRegression|FullyQualifiedName~HelpDocumentation|FullyQualifiedName~Release102Playback' \
  --logger 'console;verbosity=minimal'
```

The original extended run exposed three stale expectations caused by these moves (snapshot key and two volume-yield fixtures); they were corrected without dropping assertions. Two unrelated mismatches are already present in the starting commit, verified against its source rather than by performing another baseline build:

1. `HelpDocumentationTests.GalleryHelpMatchesLeftPlacementAndUnreducedSafetyState` at line 568 expects the exact sentence “The side panel has Post | Tags at the top left and × alone at the right”, absent from baseline Help.md.
2. `CatalogShortcutGapTests.TagsKeepThreadsAndMediaApartAndTheMenuKeyIsAGestureNextToShiftF10` at line 57 expects media/gallery contexts, while the baseline catalog already explicitly specifies media/reader/gallery for `media.tags.edit`.

These unrelated assertions have not been weakened, removed or skipped.
