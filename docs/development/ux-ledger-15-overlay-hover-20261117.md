# UX-LEDGER-15 — transient hover release

Contract: final-review protocol §1.9a (2026-11-17). Base: `1fbc8eb0b`, current origin/goal/gesamtprogramm-profile-a when this worktree was created.

## Proof case and implementation

The gallery previously re-evaluated `_lastPointerScreen` indefinitely. Leaving the native window through its right border need not deliver a final motion sample outside the post: the last interior coordinate therefore kept `_pointerInPost` true. `RefreshHoverGeometry` and `RefreshFilmstripHover` now require current gallery/root `IsPointerOver`, active-window and interaction availability before using a coordinate. Root exits, host deactivation, item replacement and interaction reconciliation release transient overlays. Release clears pointer samples, hover flags, motion clocks and filmstrip peek; closes only hover-opened post context; hides auto-hide footer and disables its hit testing. Explicitly docked/always-visible/tagging decision surfaces remain persistent. Keyboard modality is not reset by pointer release.

`TransientHover` supplies the same release lease to custom surfaces. It observes current `IsPointerOver`, effective attachment/visibility/enabled state, active/non-minimized window and covering menus/flyouts. Window deactivation, visibility/state changes, detach, DataContext replacement, scrolling and different interactions invalidate the lease. It does not consume input. Only an active hover has an 80ms reconciliation timer; ordinary leave/deactivation releases synchronously. Activation alone cannot resurrect a lease: fresh pointer entry/motion can, without a click. Subscriptions end on detach/disposal. Keyboard focus remains a separate state owned by each surface.

## Surface audit

| Surface | Verdict / release owner |
| --- | --- |
| Gallery post hover (reported RIGHT edge) | Fixed: current pointer presence gates geometry; release closes transient context, not an explicitly pinned panel. |
| Gallery footer, top-left corner chrome | Fixed: native/window exit and focus loss remove transient visibility and hit ownership; replacement retires the old sample. Keyboard ownership is preserved separately. |
| Gallery edge indicators | Fixed: clear motion on release; inactive windows paint no hover hint; expired/undrawn indicators do not intercept media clicks. Edge bands and Tab can reveal again. |
| Gallery filmstrip peek, arrows, tile highlight | Fixed: pointer presence gates peek geometry; lease releases arrows/highlight; scrolling clears hovered tile identity. Keyboard focus remains separate. |
| Filmstrip thumbnail decode/prefetch | Audited: visibility-window work, NOT hover work. Existing thumbnail window owns cancellation on virtualization, reset and retirement; intentionally not cancelled just because a visible tile loses hover. |
| Media/archive/tagging tiles and favorite affordances | Fixed shared favorite styling: window-aware `transient-pointer-over` class instead of a latched pointer pseudo-class; opacity-zero favorite controls are not hit-testable. Selection and keyboard-focus styles remain separate. Thumbnail loads are visibility-driven and already have request/identity/cancellation fences. |
| Reader attachment hover enlargement | Fixed: pointer lease calls `Close` on release; delay admission and async publication both re-check lease; existing request generation cancels/discards late results and releases bitmaps. |
| Quote references, inline quotes, preview media and highlight | Fixed: pointer lease covers reference/preview; host loss stops pending delay and media; scrolling retires preview/highlight; external popup closure disposes lease. Existing bounded reference-to-preview grace is retained while inside the window. Keyboard preview is separate and cannot publish in an unusable window. |
| Reader body spoiler hover / inline reference cursor | Fixed: release clears hover-only spoiler reveal, quote identity and cursor; explicit click-revealed spoilers remain separate. |
| Reader media sidebar close affordance | Fixed: lease-derived hover; inactive/covered surface cannot keep the close overlay hit-testable. Persistent sidebar content and playback are not retired by focus loss. |
| Reader auto-hide navigation | Fixed: surface release clears cached point, pointer/activity holds and timer; auto mode requires usable window. Always-visible mode remains persistent. |
| Rating-star preview | Fixed: per-star lease clears speculative rating on release; item replacement/release clears old preview without discarding an ordinary same-item repaint. Saved rating/favorite are unchanged. |
| Video scrub preview | Audited existing window/environment gate and generation cancellation; added shared pointer lease to cancel pending scrub work on missing leave/cover/interaction release. |
| Video volume hover | Fixed: deactivation closes volume popup and stops close timer; updates cannot reopen it in an unusable window. Explicit keyboard focus is retained separately. |
| Framework tooltips | Fixed shared policy: pointer lease closes on current hit-test/window/anchor/interaction release and rejects opening on an unusable anchor. |
| Flyouts/popovers/menus | Added shared Popup lease: close on deactivation, minimize/hide/disable, anchor detach/replacement/hide and ancestor scroll. Explicit click/keyboard editors are NOT hover-dismissed when moving from anchor into their content. Existing light dismiss and PopupEscape retain interaction ownership. |
| Reader feedback overlay hover hold | Fixed: lease releases the reading hold; informational feedback expires when unusable. Persistent loading/error channels remain operation-owned. |
| Media-focus floating window header | Fixed: shared lease spans reveal strip/titlebar, releases hover reveal on window loss; normal docked header remains persistent. |

## Deterministic verification

`GalleryOverlayActivityTests`: real geometry reconciliation with stale interior screen sample for left/right/top/bottom/corner native leaves; actual release method assertions for footer visibility, hit testing, indicator artwork, cleared samples and preserved keyboard modality.

`TransientHoverTests`: current-hit-test release, deactivation/unusable-window release, no stale activation restore, re-entry without a click, replacement/different-interaction invalidation. Integration uses a real `QuotePreviewMedia` pending decoder: a release cancels its observed token; a deliberately late bitmap completion never appears.

Existing quote media, gallery overlay/filmstrip, reader sidebar, rating and scrub tests are included in the focused lane. No existing assertion was removed or weakened and no timeout was raised.

## Executed results

- Application build: `DOTNET_PROCESSOR_COUNT=4 dotnet build MeowChan.csproj --no-restore -p:BuildProjectReferences=false -m:1`: **succeeded, 0 errors, 7 warnings** (private Avalonia APIs, nullable annotations and XAML constructor reachability).
- Final compiled-binary core lane (`GalleryOverlayActivityTests`, `TransientHoverTests`, `QuotePreviewMediaTests`): **114 passed, 0 failed, 0 skipped**, 23s. Output: [`evidence/ux-ledger-15-20261117/core-tests.log`](evidence/ux-ledger-15-20261117/core-tests.log).
- Final compiled-binary related-surface lane (filmstrip, rating row, reader navigation, scrub gate, chip/favorite styling, media-focus header, sidebar rule tests): **246 passed, 0 failed, 0 skipped**, 3m06s. Output: [`evidence/ux-ledger-15-20261117/surface-tests.log`](evidence/ux-ledger-15-20261117/surface-tests.log).
- Earlier broader lane, including the sidebar layout fixture: **345 passed, 1 failed, 1 existing skip**, 6m36s. Remaining failure: `ReaderMediaSidebarLayoutTests.DefaultToolbarStateMatchesTheDefaultScopeWithoutChangingPreferences`, line 50, expects `Ursa.Controls.OverflowMode.Never`, receives `AsNeeded`. The toolbar layout/overflow implementation was not changed here; this failure was not weakened, suppressed or declared a proven baseline failure. It remains an open acceptance finding. Existing skip: `FocusClickContractTests.GalleryFilmstripRightClickMustSelectTargetNotOnlyRetargetTheMenu`.
- A combined application-build/test-compile invocation reached the 600s command deadline before completing test execution on the heavily contended shared host. Its application build succeeded (0 errors); final test binaries were then exercised by the two explicit `--no-build --no-restore` lanes above. No test assertion timeout was changed. This successful subset does **not** erase the broader failure.
- Source-wiring and favorite-style selector assertions were updated to the new lease/selector wiring, retaining the original visual assertions. Favorite styles additionally assert hidden controls cannot own input. An initially exposed same-item rating repaint regression was fixed, not worked around.

## Coverage limits and assumptions

This is implementation plus deterministic managed state-machine/component verification, not native end-to-end acceptance (UX-E25/UX-E27). No host/shared desktop was driven. Native all-edge/corner/window-deactivation sweeps across Windows/macOS/Linux, screen-lock/workspace transitions, themes/languages, popup focus-return and performance with many realized tiles remain unverified. Full visual/layout/a11y checks (including the unrelated common-error classes) were not inferred from this targeted behavioral lane.

Interpretation: "transient" excludes explicit always-visible/docked UI and active click/keyboard popover editors; hover work excludes thumbnails whose cause is viewport visibility. Losing window usability ends hover, not playback or saved selection. Workspace/screen lock release is mapped to the platform's window deactivation/visibility notifications.

Overall release acceptance remains **incomplete pending native matrix evidence**, even if the focused deterministic lane passes.
