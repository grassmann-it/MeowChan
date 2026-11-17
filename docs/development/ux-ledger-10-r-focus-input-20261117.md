# UX-LEDGER-10 — Rate and tag starts in the tag field

## Contract

- R, the combined **Rate and tag…** menu item, rating captions and toolbar entry points start with the caret in the tag input, like Ctrl+Shift+T. This applies to Tagging, Media (grid, panel and selection), Archive/embedded and large reader, gallery Tags segment and gallery filmstrip popovers.
- `RateAndTagRequest.FocusRating` defaults to `false`. `QuickTagPopoverDecision.FocusRating` remains an explicit star-focus opt-in, not the meaning of “include a rating row”. Tagging uses a separate `includeRating` flag so its R surface retains the stars while focusing the field.
- Popover opening and lost-focus repair use the same focus policy. Without a tag field (blocked tagging), focus falls back to the rating stars, then the close button if rating is unavailable.
- Existing rating input is unchanged: Shift+1…5 rates and Shift+0 clears from the stars or an empty tag field; when the focused field contains text or has its suggestion list open, these keys stay text/suggestion input. Plain letters/digits and composition input are not converted into rating/favorite commands. Stars remain a roving Tab stop with Left/Right/Home/End and native Space/Enter activation, and pointer rating returns focus to the tag field when it owned focus before the click.
- Held opening-R repeats remain suppressed by the existing popover latch. Esc, focus hand-back, identity resolution, selection membership and persistence contracts are unchanged.

## Scope

Only rate-and-tag focus policy, entry-point arguments, related contract tests and native typing smoke expectations change. `GalleryDecisionRow` was inspected and its existing keyboard/pointer behavior is retained. Reader media activation, gallery context core and plugin signatures are not changed. Autocomplete commit, favorite layout and suggestion scrolling remain separate ledger items.

## Regression coverage

- Desktop tests cover the default request, explicit star-focus opt-in, host arguments, Tagging's separate row-inclusion contract, gallery field-before-stars ordering, rating shortcut dispatch, typing passthrough, held opening keys, Esc and focus restoration.
- The `quick-tag-native-typing` private-Xvfb/XTest smoke now requires **focus in the input before the first typed character**, including R/menu entry and the gallery R path (not merely eventual redirection of text from the stars). It also checks empty-field Shift+digit rating, Shift+digit typing once text exists, explicit star focus, arrow roving and Space activation, plus its existing video playback, suggestion/undo and close checks.
- Linux native-input evidence does not constitute Windows/macOS or IME acceptance.
