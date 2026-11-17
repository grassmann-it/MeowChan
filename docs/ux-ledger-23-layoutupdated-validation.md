# UX-LEDGER-23: LayoutUpdated lifetime regression

Scope: LayoutUpdated listeners and the two PointerFocusReclaim owners only; no CancellationTokenSource disposal changes.

## Regression

`tests/MeowChan.DesktopTests/LayoutSubscriptionLifetimeTests.cs` delivers the actual virtual attach/detach callbacks on a `MediaTilePanel` 100 times (with double-detach), and reads Avalonia's actual LayoutUpdated delegate invocation list. It requires exactly one listener while attached and zero after detaching, relative to an empty Layoutable's baseline. This is a windowless lifecycle unit test, not a GUI smoke test; it neither pumps the dispatcher nor fabricates a separate subscription counter.

Command (affected desktop project and its test project, plus project-reference prerequisites only):

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  -p:MeowChanPluginDeliveryEnabled=false --no-restore \
  --filter FullyQualifiedName~LayoutSubscriptionLifetimeTests \
  --logger 'console;verbosity=detailed'
```

### Before (production code at e2ee324ef, regression added)

```text
MediaTilePanelReturnsLayoutSubscriptionsToBaselineAfter100Reattachments [FAIL]
Assert.Equal() Failure: Values differ
Expected: 0
Actual:   1
cycles=100; baseline=0; maximum detached subscriptions=1; final=1
Test Run Failed.
Total tests: 1
     Failed: 1
```

### After (same regression and assertions)

```text
Passed MeowChan.DesktopTests.LayoutSubscriptionLifetimeTests.MediaTilePanelReturnsLayoutSubscriptionsToBaselineAfter100Reattachments
Standard Output Messages:
 cycles=100; baseline=0; maximum detached subscriptions=0; final=0
Test Run Successful.
Total tests: 1
     Passed: 1
```

The original defect retains one subscription; it does not add another delegate every time this particular control attaches. The test deliberately checks return to baseline rather than falsely claiming linear delegate growth.

## Sidebar investigation

Neither subscription is dead on this base, and they are **not on the same control**:

- `InitializeMediaSidebar`: the listener on `ReaderMediaSidebarToggle` refreshes its live action, including `CanToggleMediaSidebar` (filename search/dialog state), localized label, shortcut tooltip and responsive-width hint.
- `EnsureMediaSidebar`: the listener on the `ReaderMediaSidebar` instance runs `AlignMediaSidebarFrame`, which still translates the posts box and sidebar into their shared host and adjusts vertical insets in `ArchivePage.MediaSidebarAlignment.cs`. The fixed header height does not determine the posts box's changing position and height.

Both are retained, subscribed once on their own control's attach and removed on its detach. This matters when the reader pane/sidebar moves to the large-reader host independently of the page.
