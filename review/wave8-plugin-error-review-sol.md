# Wave 8 — L8-01 Plugin error-banner fix: independent review

## Scope and evidence boundary

Read-only review of the current working-copy delta in the five requested paths,
against:

- `review/wave8-l10n-footer-sol.md`, especially L8-01 and the explicitly excluded
  L8-02 probe-output problem;
- `review/wave8-plugin-error-fix-sol.md`, including its stated changed-file list,
  raw/structured-text boundary, lifecycle claims and test limitations.

No source edits, builds, tests, application startup, Git operations, RUNSTATE/lease
updates or state changes were performed. This report is the only file written.
The current source files were read directly; the hashes below identify the reviewed
working-copy bytes and are not a repository-wide freeze.

| File | SHA-256 |
|---|---|
| `Desktop/Views/Plugins/PluginsPage.axaml.cs` | `83216b46b36d9e6b83b944d4a52122fd1837a043ed22fba193c3661a79bbc14d` |
| `Desktop/Views/Plugins/PluginsPage.Testing.cs` | `157d87ab74f36a7c4d4b65b668e05331af7f6129e10b0e70d0ef7a944d5f0759` |
| `Desktop/Views/Plugins/PluginsPage.Migration.cs` | `22ba0ad1c09e277a8a84e03f0f6c9a20da109bb265f4932fc8cb4a5819e4f213` |
| `Desktop/Views/Plugins/PluginsPage.Actions.cs` | `e3d37e9432c2eec4419b23d865d92514f907c750c58a3dc9b5381852b2c67ff5` |
| `tests/MeowChan.DesktopTests/PluginErrorLanguageRefreshTests.cs` | `8ae67c01cb8cd30a1d7643b6a30905d815840445e7bfc1a5709fb3de7345b4c6` |

## Verdict

**No concrete P0/P1 found in the bounded L8-01 delta.** The implementation matches
the intended narrow fix: app-owned plugin error wrappers retain a translation key
and arguments, raw worker/compiler/exception/probe text remains opaque, and the
language callback rerenders the existing state instead of clearing it.

The remaining risks are verification gaps and one deliberately documented design
limit rather than a demonstrated regression:

- the argument array is copied shallowly; current call sites pass strings or the
  `PluginBuildStatus` enum, but mutable argument objects would remain live;
- the new tests are pure state/source-contract tests, not an Avalonia banner,
  dispatcher, focus, lifetime or action-behavior test;
- action predicates appear unchanged by inspection, but the source assertions do
  not prove behavioral parity for every command/reason branch;
- L8-02 (localized framing inside cached probe output) remains intentionally
  unfixed and must not be counted as fixed by L8-01.

## 1. Raw text versus structured host keys

### Structured host-owned error paths

The following current call sites correctly carry provenance instead of passing an
already-rendered localized string through `Error(string?)`:

| Location | State passed | Assessment |
|---|---|---|
| `PluginsPage.axaml.cs` | `plugins.unavailable` | Correct host key; no arguments. |
| `PluginsPage.Testing.cs` | `plugins.noArtifact` | Correct host key. |
| `PluginsPage.Testing.cs` | `plugins.buildFailed`, `result.Status` | Correct wrapper plus a non-localized enum argument. |
| `PluginsPage.Testing.cs` | `plugins.invalidUrl` | Correct host key. |
| `PluginsPage.Testing.cs` | `plugins.editorFallback`, `exception.Message` | Correct localized wrapper; exception detail remains an opaque argument. |
| `PluginsPage.Migration.cs` | `plugins.repair.finishDraft` | Correct host key. |
| `PluginsPage.Actions.cs` | result of `PluginActionUnavailableReasonKey(action)` | Correctly resolves the reason before translation; no reverse lookup of menu text. |

`PluginErrorState.Localized` stores `Key` and a cloned argument-array. `Render()`
passes that array to `Texts.T`, so a language/catalog change resolves the current
catalog at render time. This also handles a same-language catalog reload, not only
an `en`/`es`/`fr`/`ru` language-code change.

### Raw paths remain raw

The following paths still use `Error(string?)` and are consistent with the L8-01
contract:

- `exception.Message` from catalog refresh, folder/editor operations, command
  failures, migration failures and editor-save failures;
- field-validation messages assembled from worker/compiler error messages;
- `FormatProbe` output for `ThreadFailure`/`CatalogFailure`;
- compiler/editor/worker details rendered elsewhere in the plugin page.

There is no current `Error(Texts.T(...))` call in the reviewed plugin files. A raw
string that happens to equal `plugins.invalidUrl`, or contains a key-looking token,
is not reverse-mapped. The test file explicitly covers that distinction.

The `FormatProbe` failure branch is worth keeping distinct from the excluded L8-02
issue: for `ThreadFailure` and `CatalogFailure` it returns `Code + ": " + Message`,
not a localized host wrapper, so passing it as raw error text preserves the existing
semantics. Other probe result presentation can still retain localized framing in
`PluginProbeResult`; this audit does **not** mark cached probe presentation fixed.

## 2. State lifetime, language callback and focus projection

### Correctly retained state

`_errorState` is page-instance state and is not reconstructed from the rendered
`TextBlock`. `OnLanguageChanged` dispatches onto the UI thread, checks `_ended`,
then calls `Localize(); RenderError(); RenderList(); RenderDetails();`. It does not
call `Error(null)` or assign `_errorState`.

The existing clear points remain explicit and semantically understandable:

- `BeginOperation()` clears the previous page error before a new operation;
- changing the selected plugin clears it;
- removal of the selected entry clears it;
- `EndLifetime()` unsubscribes and makes queued callbacks harmless through `_ended`.

`Deactivate()` cancels operations but does not clear `_errorState`; that is necessary
for the L8-01 navigation path where a failed plugin remains selected after leaving
and returning to the page. Retaining the error across deactivation is therefore
consistent with the stated fix, not an accidental stale-render regression.

### Accessibility and focus behavior

`RenderError()` updates all four presentation facets from the same rendered value:

- `PluginPageError.Text`;
- `Focusable` based on non-empty text;
- automation name;
- `PluginErrorBanner.IsVisible`.

This preserves the existing accessible banner projection while replacing only its
language-dependent text. No focus is explicitly stolen by a language switch, which
is the correct default for a refresh. A focused error text control should remain the
focused control while its text/name changes.

There is one unverified runtime edge: language refresh also closes materialized
repair/action popups. `ClosePluginActionMenu()` is called without focus restoration,
and closing a repair preview window has no explicit return-focus operation in this
callback. That may affect focus after a language switch while a popup is open, but it
is a pre-existing stale-materialized-surface policy and not an L8-01 error-state
mismatch. It needs a later UI smoke check; it should not be “fixed” by clearing the
error or reopening a menu with old text.

A second runtime-only edge is a queued `Texts.Changed` callback after deactivation:
the callback is guarded by `_ended`, but not by visibility. Updating a hidden page is
safe for the retained state and consistent with existing owner refresh patterns;
actual Avalonia dispatcher behavior remains unverified here.

## 3. Action availability and semantic preservation

The extraction in `PluginsPage.Actions.cs` is semantically conservative on source
inspection:

- `PluginActionUnavailableReasonKey` retains the lifecycle/visibility/enabled/
discard checks;
- the special `plugins.actions` menu check remains before the busy check;
- busy, generation, catalog, selected-key, inclusion and captured-target matching
  predicates remain the same shape;
- the command switch still returns the same reason categories for enable/disable,
  candidate selection, rollback, settings, watches, grants and details;
- `PluginActionUnavailableReason` remains the translating adapter for button/menu
  help text;
- failed execution now sends the already-resolved key to `ErrorLocalized` rather
  than trying to reverse-map a rendered reason string;
- successful command dispatch still uses the captured immutable
  `PluginActionInvocation` and the existing command switch.

No action is enabled merely because a reason changed language, and no new action,
write operation, retry, candidate substitution or selection mutation is introduced
by the fix. `PluginActionTarget` is an immutable record snapshot; its nested build
and release records are also records, so the action-state capture itself is not a
mutable-argument concern.

The limitation is evidentiary: `PluginErrorLanguageRefreshTests` checks that the
key resolver is called and that particular source fragments exist, but it does not
execute every reason branch or compare enabled/disabled results before and after the
refactor. The existing `PluginActionTargetTests` cover target identity separately,
but they are not an action-availability parity suite.

## 4. Mutable arguments and formatting safety

The constructor clones the `object?[]` container:

```csharp
_arguments = (object?[])arguments.Clone();
```

This correctly prevents a caller from replacing an array slot after
`ErrorLocalized(...)`. The new test exercises exactly that case. It does **not**
deep-clone argument values, and `Texts.T` formats those values later on every
render. Therefore a mutable object passed as an argument would still be observed
in a later language refresh.

For the current host call sites this is acceptable and matches the fix report:

- `result.Status` is an enum value;
- `exception.Message` is a string;
- the other current structured calls have no mutable arguments.

Recommended future guard, not a finding against this delta: keep the structured
error API restricted to immutable scalar/string/enum arguments, or snapshot any
future structured value before storing it. Do not deep-clone arbitrary plugin or
exception objects. A focused future test should use a mutable reference object only
if the API is intentionally widened; otherwise a type-level/scalar contract is the
safer boundary.

## 5. Test review and expected risks

`PluginErrorLanguageRefreshTests.cs` provides useful bounded coverage for:

- EN → ES/FR/RU → EN key retention;
- current-catalog rendering;
- shallow array-slot copying;
- raw key-looking text and raw text equal to an English translation;
- callback rerender/clear contracts;
- current host call-site provenance and action-key routing.

Expected risks when the suite is eventually executed:

1. **Static source anchors are brittle.** `Between(...)` depends on exact method
   signatures and neighboring method names. Harmless refactors can fail the test;
   conversely, a source string can remain while runtime wiring is broken.
2. **No page construction.** The test never creates `PluginsPage`, `Banner`,
   `TextBlock`, a dispatcher callback or a real language event subscriber. It cannot
   establish that the UI control exists, is attached, or receives the rerender.
3. **No focus/lifetime exercise.** There is no test for a focused error control,
   queued callback after `EndLifetime`, deactivation/reactivation, popup closure or
   returning focus after language change.
4. **No action-behavior exercise.** The test does not invoke disabled actions or
   assert that every command predicate has identical availability before/after the
   extraction. The test therefore guards provenance more strongly than semantics.
5. **Global localization state.** The collection and reset logic reduce interference,
   but the tests mutate process-global `Texts` state. An eventual failure may be
   order-/fixture-sensitive if another test bypasses the collection discipline.
6. **Catalog assumptions.** The test assumes the six chosen keys exist and differ
   across the bundled languages. It is useful coverage for the bundled packs, but
   not a complete key/format contract or translation-quality check.
7. **No missing-key/null contract.** There is no direct test for a missing key,
   empty localized value, empty raw value through the page setter, repeated
   `Texts.Changed`, or a mutable nested argument. Current production call sites do
   not make these required for L8-01 acceptance.

These are expected coverage boundaries, not test failures; no test result is claimed
in this review.

## 6. Findings and handoff

### Findings

- **P0:** none found.
- **P1:** none found in the requested L8-01 scope.
- **P2:** no confirmed production defect. The shallow argument-copy limitation is
  a future API-shape risk already constrained by current immutable call-site values.
  Runtime focus/dispatcher/action parity remains unverified rather than disproven.

### Handoff

L8-01 is **source-review plausible but not runtime-verified**. Before calling the
package complete, a later bounded test/QA run should at minimum exercise:

1. a materialized host-owned banner, language change, and return from deactivation;
2. a raw worker/exception/probe error through the same sequence, asserting byte-for-
   byte text preservation;
3. action disabled reasons before and after language changes, including stale target,
   busy, no candidate, no rollback, no settings, no watches and no grants;
4. callback delivery after `EndLifetime` and focus behavior when an action menu is
   closed by refresh;
5. bundled key formatting with `plugins.buildFailed` and `plugins.editorFallback`
   arguments.

Do not expand this handoff into the separate L8-02 cached-probe redesign. The current
fix does not and should not translate/split cached probe strings heuristically.
