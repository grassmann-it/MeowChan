# UX-LEDGER-23: LayoutUpdated lifetime regression

## Integration with PRs #52 and #56

Merged `origin/goal/gesamtprogramm-profile-a` at `84a710b8b` into the published branch (no rebase). While validating, the shared target ref advanced again to `53981984a` (CTS lifetime work and smoke-contract updates); merged that target too, without conflicts. Its CTS changes auto-merged with the independent layout wiring in MediaCounts/MediaSidebar. Final validation restored the newly added Avalonia.Headless test dependency before building.
Conflicts were in `GalleryVideoPlayer.cs`, `GalleryViewer.Overlay.cs` and `GalleryViewer.cs`.
PR #52 (`9f816faaf`) already owns the reclaim handles, disposes the viewer's list at terminal `StopAsync`, and disposes the player's handle on the UI thread alongside transport retirement. The branch's equivalent `_overlayPointerFocusReclaims` / `_transportPointerFocusReclaim` mechanism is redundant and was dropped in favor of #52's implementation, not layered on top. All three files and `PointerFocusReclaim.cs` match the target exactly. `PointerFocusReclaim.IsTextInput` remains the shared text-input predicate, including #52's popup-unsubscription and deferred-callback guards. The owner-lifetime regression tests are retained to verify the consolidated mechanism.

PR #56's command catalog and gallery shortcut scheme are unchanged from the target.

## Layout subscription audit

Each listener uses a named handler, a guarded subscription flag, and idempotent removal on its **own visual owner's** detach, following `ReaderUnreadLineHost`. Reattachment subscribes once again.

| Original audit site | Visual owner | Handler |
| --- | --- | --- |
| `SettingsEditor.Search.cs:155` | `SettingsContentColumn` | `OnSearchPanelLayoutUpdated` |
| `ArchivePage.Feedback.cs:35` | `ArchiveSplit` | `OnStatusOverlayLayoutUpdated` |
| `ArchivePage.MediaCounts.cs:69` | archive page | `OnMediaCountLayoutUpdated` |
| `ArchivePage.MediaSidebar.cs:85` | `ReaderMediaSidebarToggle` | `OnSidebarToggleLayoutUpdated` |
| `ArchivePage.MediaSidebar.cs:161` (shifted on current target) | `ReaderMediaSidebar` | `OnSidebarFrameLayoutUpdated` |
| `ArchivePage.Reader.cs:229` | `ReaderContentSurface` | `OnReaderNavigationLayoutUpdated` |
| `MenuLayout.cs:201` | menu | `OnMenuLayoutUpdated` |
| `ArchiveAttachmentView.cs:447` | attachment view | `OnVisibilityLayoutUpdated` |
| `MediaTilePanel.cs:40` | tile panel | `OnLayoutUpdated` |

## Sidebar verdict: both subscriptions remain live

They are **not on the same control** and neither is dead:

- `InitializeMediaSidebar` observes the live toolbar toggle (`ReaderMediaSidebarToggle`, still declared in `ArchivePage.axaml`). `RefreshMediaSidebarToggle` updates checked/enabled state, localized label, shortcut tooltip and responsive-width hint. It observes filename-search/dialog and host state which is not all covered by the preference-change refresh.
- `EnsureMediaSidebar` observes the sidebar itself. `AlignMediaSidebarFrame` in `ArchivePage.MediaSidebarAlignment.cs:34` translates the posts box and sidebar into the shared host and adjusts top/bottom insets after layout. A fixed header height does not determine the posts box's changing position and height. The alignment was introduced by `f2312c0e9` and remains the sole caller path to that alignment method.

PRs #19 (`39bc0c6e6`) and #24 (`afc35bc4e`) restructured **Settings** headers/search anchoring; neither merge changed `Desktop/Views/Archive`. They did not remove either sidebar owner or responsibility. Both subscriptions are retained and detached with their respective controls when the reader moves between embedded and large-reader hosts.

The previously dirty `ReaderSidebarAlignmentTests.cs` change belongs: it replaces the old anonymous-handler source assertion with stronger assertions for the named alignment handler, subscription, unsubscription, and attach/detach wiring. It is retained without weakening assertions.

## Leak regression methodology

`tests/MeowChan.DesktopTests/LayoutSubscriptionLifetimeTests.cs` delivers the actual virtual attach/detach lifecycle callbacks to a `MediaTilePanel` 100 times, double-detaching each cycle. It checks Avalonia's actual `LayoutUpdated` delegate list against an empty control's baseline. Deterministic layout pulses invoke those real delegates and count completed invocations, both attached and detached, followed by 100 extra detached pulses. No production counter or simulated subscription is used. This is windowless lifecycle testing, not a GUI smoke suite or a managed-heap collectability claim.

The original code retains **one** listener after detach; it does not accumulate one new listener per reattachment. The leak evidence is continuing handler invocation on detached controls (and the retained subscription), not a fabricated linear increase in delegate count.

Before/after use the identical regression and assertions. For the before run only `Desktop/MediaTilePanel.cs` is temporarily replaced by the unmodified target version at `84a710b8b` (pre-fix for layout lifetimes); the fixed file is restored before the after build.

Command (desktop test project, desktop application and project-reference prerequisites only):

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -p:MeowChanPluginDeliveryEnabled=false --no-restore \
  --filter FullyQualifiedName~LayoutSubscriptionLifetimeTests \
  --logger 'console;verbosity=detailed'
```

### Before: pre-fix `MediaTilePanel` at `84a710b8b`

```text
Failed MeowChan.DesktopTests.LayoutSubscriptionLifetimeTests.MediaTilePanelReturnsLayoutSubscriptionsToBaselineAfter100Reattachments
Assert.Equal() Failure: Values differ
Expected: 100
Actual:   200
Standard Output Messages:
 cycles=100; baseline=0; maximum detached subscriptions=1; final=1
 attached invocations=100; detached invocations after cycles=100; after 100 more layout pulses=200
Test Run Failed.
Total tests: 1
     Failed: 1
```

### After: fixed `MediaTilePanel`, identical test

```text
Passed MeowChan.DesktopTests.LayoutSubscriptionLifetimeTests.MediaTilePanelReturnsLayoutSubscriptionsToBaselineAfter100Reattachments
Standard Output Messages:
 cycles=100; baseline=0; maximum detached subscriptions=0; final=0
 attached invocations=100; detached invocations after cycles=0; after 100 more layout pulses=0
Test Run Successful.
Total tests: 1
     Passed: 1
```

### Affected regression selection

The affected desktop application and test project build successfully (existing compiler/analyzer warnings). No full solution build or GUI smoke suite was run.

A broader desktop-only selection covering layout lifetimes, reclaim owners, text-input classification, sidebar/alignment, menus, settings search, navigation and #56 shortcut invariants produced **366 passed, 1 failed (367 total)** after the final target merge (earlier target: 365 passed / 1 failed). Both reclaim-owner lifetime tests and the layout invocation regression pass on the final merged branch. The failure is `ReaderMediaSidebarLayoutTests.DefaultToolbarStateMatchesTheDefaultScopeWithoutChangingPreferences`, line 50: toolbar overflow mode expected `Never`, actual `AsNeeded`.

This failure reproduces in isolation and with **all Desktop production sources restored to exact target `84a710b8b`**, using the unchanged test assertion. It is a pre-existing toolbar policy mismatch, not introduced by layout detachment. No assertion was weakened or skipped and no unrelated toolbar behavior was changed.

The focused final selection is green: **56 passed / 0 failed**, using:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -p:MeowChanPluginDeliveryEnabled=false --no-build --no-restore \
  --filter 'FullyQualifiedName~LayoutSubscriptionLifetimeTests|FullyQualifiedName~PointerFocusReclaim|FullyQualifiedName~TextInputPredicateTests|FullyQualifiedName~ReaderSidebarAlignmentTests' \
  --logger 'console;verbosity=detailed'
```

The broader selection adds `ReaderMediaSidebar`, `MenuLayoutTests`, `SettingsSearch`, `SettingsUniformLayoutTests`, `ArchiveMediaCount`, `ArchiveFeedback`, `MediaTilePanel`, `ReaderNavigationLayoutTests`, `GalleryShortcutSchemeTests` and `DefaultShortcutInvariantTests` with the same `FullyQualifiedName~` OR-filter syntax. Its known failure remains reported above, not hidden by the focused run.
