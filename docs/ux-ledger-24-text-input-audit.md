# UX-LEDGER-24 — text-input ownership audit

Base inspected: `e2ee324ef` (`origin/goal/gesamtprogramm-profile-a`, 2026-11-17).
This is the pre-refactor analysis; line references below refer to that base.

## Rule and scope

`Desktop/Controls/PointerFocusReclaim.cs:103-104` already exposes an **internal** `IsTextInput(Visual)` with exactly TextBox, NumericUpDown, ComboBox and Ursa AutoCompleteBox (including subclasses, disabled and read-only instances). No visibility change is needed. `Resolve:86-101` walks ancestors only to its scope, shields text/scrollbars/exempt subtrees and lets only explicitly opted-in sliders reclaim. This rule must not become a generic interactive-control classifier. Ancestor boundaries and key-specific exceptions remain local.

The request says eleven copies, but enumerates **twelve expressions**. All twelve were inspected. The current base also has a third reader expression (`KeyBelongsToNativeEditor:1041`), which must be consolidated too. Reader line numbers moved since the earlier audit because embedded-reader activation has already landed.

## Per-site verdict (needed / divergence / intentionality)

| Site on base | What it actually needs | Divergence and verdict | Planned treatment |
|---|---|---|---|
| Commands/CommandBindings.cs:518, MatchesGalleryTransport | Nearer native editors must block a transport override before reaching the specifically owned seek/volume slider. | Core list matches; ScrollBar/MenuItem are intentional native key owners. Other following tests are key-dependent, not text classification. | Shared predicate plus explicit ScrollBar/MenuItem; preserve boundary/key tests. |
| Commands/CommandRouter.cs:91, IsNativeInput | Keep commands out of editors; separately reserve native slider/list/activation/selection keys. | Same core and ScrollBar/MenuItem as above; intentional. | Shared predicate plus explicit extras. Do not classify sliders/buttons as text. |
| Shell/MainWindow.Selection.cs:22, IsNativeShortcutSource | Broad conservative native shortcut shield, including read-only selectable text and widgets. | NumericUpDown omission is accidental: numeric editing is typing even when source is the outer number field. SelectableTextBlock/Button/MenuItem/Slider/TabItem intentionally remain native here. | Shared core (adds numeric root protection) plus existing extras. |
| Shell/MainWindow.Selection.cs:102, IsListCommandSource | Admit only list-owned commands before reaching the list boundary. Editors/headers/widgets keep keys. | Same missing numeric root; explicit DataGridColumnHeader is deliberate. | Shared core plus all existing extras; preserve list-first boundary. |
| Views/Archive/ArchivePage.Selection.cs:91, IsRowButtonPath | Right click on thumbnail/favourite buttons belongs to the thread row, not the open thread's media menu. | Button is **deliberately not blocked** (tracked independently). Slider/selectable text/menu/tab/header/scrollbar shielding intentional. Missing numeric root accidental. | Shared core plus existing blockers; keep button and row tracking. |
| Views/Archive/ArchivePage.Reader.cs:1096-1098, ReaderMediaBelongsToNativeControl (was ~1061) | Prevent Space media activation from swallowing controls inside posts; post body selection has a separate policy. | Broad widget list intentional: buttons, menu, sliders, scrollbars, tabs, grids, expanders, tags, ratings and selecting controls have native activation. Core already complete. | Shared core plus explicit widgets; preserve ReaderBodyTextBlock selection and posts boundary. |
| Views/Archive/ArchivePage.Reader.cs:1267-1269, ReaderArrowBelongsToNativeControl (was ~1207) | Arrow routing: native widgets retain navigation; post list yields Up/Down but keeps Home/End/Page keys. | Different from Space intentionally: buttons/expanders/tags/ratings need activation keys, not all arrows. Core complete. | Shared core plus existing arrow owners; preserve TabControl and posts special cases. |
| Views/Settings/SettingsEditor.Search.cs:184, OnSlashFocusesSearch | Bare slash focuses search unless editing; historically a closed ComboBox yields slash. | TextBox/NumericUpDown plus **open** ComboBox, no autocomplete. Open-only selector distinction may be deliberate search shortcut policy; preserve rather than silently disable slash on closed selectors. Missing autocomplete protection accidental (typeahead/editor). | Shared core with explicit closed-ComboBox exclusion. Add autocomplete protection. |
| ReaderSidebarStepKeys.cs:36-38 | Alt+Shift arrows may escape only reader-owned posts/strip, never nested native widgets or another list. | Extra Slider/ScrollBar/MenuItem/SelectingItemsControl/DataGrid/TabItem and selected text deliberate; core complete. | Shared core plus explicit extras; keep boundary order. |
| MediaActions/MediaMenuBuilder.cs:317-318, IsNativeTextSource | Do not replace native editor/selection/slider context menus with media menu. | Slider/SelectableTextBlock intentional extras; NumericUpDown omission accidental (root and spinner parts must retain editor menu ownership). | Shared core plus explicit Slider/SelectableTextBlock. |
| Controls/ItemActivation.cs:30-32 | Double click on an item's interactive part must not activate the item (e.g. selecting a word). | Entire broad non-text list intentional; core complete. | Shared core plus every existing non-text type, unchanged. |
| Views/Rules/RulesPage.axaml.cs:77-78, Escape handler | Open selector suggestions consume Escape before dismissing a decision/wizard. **Not** a general typing guard. | Only open ComboBox and autocomplete intentionally shield Escape; TextBox/NumericUpDown and closed ComboBox must still allow wizard dismissal. | Shared core plus explicit suggestion-surface refinement (no widening to all editors). |
| Additional current-base copy: Views/Archive/ArchivePage.Reader.cs:1041, KeyBelongsToNativeEditor | Inline reader paging must not steal keys from editors/menu/sliders/scrollbars. | Missing autocomplete accidental; other extras intentional. | Shared core plus MenuItem/Slider/ScrollBar. |

Intentionality here is inferred from actual consumers, comments and surrounding routing, not from the type list alone. In uncertain cases (closed selector slash, Escape) existing behaviour is retained explicitly. The consolidation is not a wholesale rewrite of native-key ownership and does not retroactively claim to fix every historical symptom; it removes the duplicated core type lists that permit them to recur.

## Handle ownership leak

Confirmed discarded `Attach` results at GalleryViewer.Overlay.cs:324 (two roots: viewer and native overlay) and GalleryVideoPlayer.cs:582 (transport panel). Retain both viewer leases until terminal `StopAsync`, not suspend/reparent; retain the player lease until `DisposeAsync` on the UI thread. Removing a subtree alone must not be treated as disposal, since the transport is reparented and reused. Also ensure already deferred reclaim callbacks cannot run after disposal.

## Proving validation plan

Add a control-type table covering the four text classes, subclasses/read-only inputs and the intentional non-text widgets. Exercise actual routing/activation/menu/list/reader guards on direct sources and descendants where feasible; assert per-site deliberate exceptions separately, not false equality between 'native input' and 'text input'. Cover restricted slash/Escape semantics, lease disposal and deferred callbacks. Run affected DesktopTests input/focus/routing tests and only the `tag-autocomplete-commit` desktop smoke scene. No solution build or full smoke suite.
