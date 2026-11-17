# UX-LEDGER-10 — Rate and tag starts in the tag field

## Contract

- R, the combined **Rate and tag…** menu item, rating captions and toolbar entry points start with the caret in the tag input, like Ctrl+Shift+T. This applies to Tagging, Media (grid, panel and selection), Archive/embedded and large reader, gallery Tags segment and gallery filmstrip popovers.
- `QuickTagInitialFocus` explicitly distinguishes `TagInput` from `Rating`; request/decision defaults and every general rate-and-tag entry use `TagInput`. Decision-row presence controls rating visibility independently; `Rating` remains available for intentional star-focus callers. Selection delegates carry the same enum rather than an overloaded boolean. Tagging uses a separate `includeRating` flag so its R surface retains the stars while focusing the field.
- Popover opening and lost-focus repair use the same focus policy. Without a tag field (blocked tagging), focus falls back to the rating stars, then the close button if rating is unavailable.
- Existing rating input is unchanged: Shift+1…5 rates and Shift+0 clears from the stars or an empty tag field; when the focused field contains text or has its suggestion list open, these keys stay text/suggestion input. Plain letters/digits and composition input are not converted into rating/favorite commands. Stars remain a roving Tab stop with Left/Right/Home/End and native Space/Enter activation, and pointer rating returns focus to the tag field when it owned focus before the click.
- Held opening-R repeats remain suppressed by the existing popover latch. Esc, focus hand-back, identity resolution, selection membership and persistence contracts are unchanged.

## Scope

Only rate-and-tag focus policy, entry-point arguments, related contract tests and native typing smoke expectations change. `GalleryDecisionRow` was inspected and its existing keyboard/pointer behavior is retained. Reader media activation, gallery context core and plugin signatures are not changed. Autocomplete commit, favorite layout and suggestion scrolling remain separate ledger items.

## Regression coverage

- Desktop tests cover the default request, explicit star-focus opt-in, host arguments, Tagging's separate row-inclusion contract, gallery field-before-stars ordering, rating shortcut dispatch, typing passthrough, held opening keys, Esc and focus restoration.
- The `quick-tag-native-typing` private-Xvfb/XTest smoke now requires **focus in the input before the first typed character**, including R/menu entry and the gallery R path (not merely eventual redirection of text from the stars). It also checks empty-field Shift+digit rating, Shift+digit typing once text exists, explicit star focus, arrow roving and Space activation, plus its existing video playback, suggestion/undo and close checks.
- Linux native-input evidence does not constitute Windows/macOS or IME acceptance.

## Verified results

- Rebased the inherited patch onto `1fbc8eb0b` (PR #27); retained the upstream gallery header counter placement. The MediaPage conflict preserves upstream multi-selection favourite handling. After publishing the first verified change, merged `535b5515a` (through PR #32) without rewriting the published branch; the autocomplete editor-only focus repair is preserved.
- Final desktop regression group: **237 passed, 0 failed**, including RateAndTag, QuickTagPopover, QuickTagSurface, GalleryRatingPanel and MediaViewsDeadAffordance tests. Run with `dotnet test tests/MeowChan.DesktopTests --no-build --no-restore --filter 'FullyQualifiedName~RateAndTag|FullyQualifiedName~QuickTagPopoverTests|FullyQualifiedName~GalleryRatingPanelTests|FullyQualifiedName~MediaViewsDeadAffordanceTests|FullyQualifiedName~QuickTagSurfaceTests'`.
- Final native registry scenario: **1 passed, 0 failed, 0 skipped**, isolated Linux Xvfb/Openbox/XTest, scale 1. Every opening/typing context passes, including immediately typed text, visible R rating rows, empty-field Shift+digit rating, explicit star focus/Right/Space, text editing, undo, Esc and focus restoration. Command: `python scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Debug/net10.0/MeowChan.DesktopSmoke.dll --registry --only quick-tag-native-typing --timeout 300 --output <fresh-evidence-directory>`.
- The runner's default 60-second **whole-scenario** budget initially expired after progressing through three opening cases on a heavily loaded host (load 61–74 on 16 CPUs). The explicitly bounded 300-second run accommodates the complete multi-context scenario; no application/test waits or assertions were relaxed. Concurrent builds initially collided in Avalonia intermediates; subsequent builds ran serially with capped parallelism.
- Native input exposed a genuine shifted-digit edge case: X11 may omit `KeySymbol`, so the frame swallowed Shift+3 even after the rating route correctly declined it while typing. `TextEntryKeys` now leaves unmodified/Shift number-row text generation to the native editor without guessing layout-dependent punctuation; six symbol-less digit tests cover Shift+0…5. Native Shift+3 produces `r#`, while empty-field Shift+3 still rates.
- The existing native undo check raced the session's published undo step against `AddTypedAsync` clearing its draft. It now waits for the actual empty, non-busy field and asserts cleanup before sending Ctrl+Z; all prior assertions remain, with additional cleanup checks.
- Both test projects build successfully with existing repository warnings. The initial unrestricted desktop attempt hit the concurrent Avalonia build collision; a later combined build/test command exceeded its command budget. The scoped final suite above is the acceptance result, not a claim that the entire desktop suite passed.
