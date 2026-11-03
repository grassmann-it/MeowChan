# Wave 8 — L8-01: Plugin error-banner language refresh

## Bounded implementation

Only L8-01 (the main plugin error banner) is implemented here. Existing WIP is
preserved. Probe output/caching (L8-02), reader groups, destructive confirmations,
repair dialogs, catalogs and language-pack contents are not redesigned or changed.

## Changes

- `Desktop/Views/Plugins/PluginsPage.axaml.cs`: retain explicit `PluginErrorState`.
  Host-owned errors keep a translation key and a copied argument array; raw errors
  keep opaque text instead. `Error(null)` still explicitly clears the state.
  Both setters share `RenderError()`, which reapplies text, focusability, matching
  automation name and banner visibility. The existing dispatched, lifetime-guarded
  language callback rerenders the current error without clearing or replacing it.
- `Desktop/Views/Plugins/PluginsPage.Testing.cs`: no-artifact, failed-build,
  invalid-URL and editor-fallback banners pass key + arguments. Compiler status and
  exception details remain arguments, not translation keys. Raw exceptions,
  validation messages and probe failure output keep their existing raw path.
- `Desktop/Views/Plugins/PluginsPage.Migration.cs`: the finish-draft banner retains
  its key. Repair preview/dialog and raw exception behavior stay unchanged.
- `Desktop/Views/Plugins/PluginsPage.Actions.cs`: source inspection confirmed that
  **all current unavailable-reason branches are fixed app-owned keys**. Extract
  `PluginActionUnavailableReasonKey()` without changing predicates or command
  semantics; retain the translating string adapter for menu/help callers. Failed
  invocation passes the key directly to the localized banner, never a reverse
  lookup of menu text.
- `tests/MeowChan.DesktopTests/PluginErrorLanguageRefreshTests.cs`: new pure-state
  regression cases for EN → ES/FR/RU → EN, parameter preservation/array-copying,
  unchanged raw/key-looking text, and raw text equal to an English translation.
  Source contracts cover callback retention, shared accessibility/visibility
  projection, explicit clear/replacement and host/action call-site provenance.

## Semantics and limits

Language refresh does not dismiss meaningful errors or start operations again.
Existing explicit lifecycle clears (new operation, changed selection or removed
selection) are unchanged. Rendering uses the current catalog even when catalogs
reload without changing the language code. Raw worker/compiler/exception text is
never reverse-mapped or translated; an app-owned wrapper alone is translated.
The retained argument array is copied, not deep-cloned; current arguments are
immutable exception strings and compiler status values.

The test additions are pure CLR-state and source-wiring coverage, not live Avalonia
banner, native-window or translation-quality verification. Cached probe output and
other excluded surfaces remain separate work.

## Validation status

Implementation and source inspection only. **No builds or tests were run.** No Git
commands, commits, RUNSTATE changes, snapshots, source freezes or cleanup actions
were performed. There is no runtime/UI or passing-test claim for this delta.

## Exact changed-file list for this implementation

1. `Desktop/Views/Plugins/PluginsPage.axaml.cs`
2. `Desktop/Views/Plugins/PluginsPage.Testing.cs`
3. `Desktop/Views/Plugins/PluginsPage.Migration.cs`
4. `Desktop/Views/Plugins/PluginsPage.Actions.cs`
5. `tests/MeowChan.DesktopTests/PluginErrorLanguageRefreshTests.cs` (new)
6. `review/wave8-plugin-error-fix-sol.md` (new)


## Final handoff — product-code stable

Finalized at **2026-11-03T22:21:11+01:00**. This finalization changed only this report;
all four product files and the new test file were read-only. No scope expansion
or product/test edits accompany this handoff. Root owns the upcoming full Desktop
run; its outcome is **pending / not observed here**. Implementation/report handoff
is complete, but runtime acceptance is not asserted.

### Live source binding (SHA-256)

Hashes are of the actual live file bytes, not a Git baseline, frozen source copy
or build output. They bind this report to precisely the L8-01 implementation and
test file consumed by the root runner. Product/test hashes were reread after the
report write to check for drift during finalization.

| Live file | SHA-256 |
| --- | --- |
| `Desktop/Views/Plugins/PluginsPage.axaml.cs` | `83216b46b36d9e6b83b944d4a52122fd1837a043ed22fba193c3661a79bbc14d` |
| `Desktop/Views/Plugins/PluginsPage.Testing.cs` | `157d87ab74f36a7c4d4b65b668e05331af7f6129e10b0e70d0ef7a944d5f0759` |
| `Desktop/Views/Plugins/PluginsPage.Migration.cs` | `22ba0ad1c09e277a8a84e03f0f6c9a20da109bb265f4932fc8cb4a5819e4f213` |
| `Desktop/Views/Plugins/PluginsPage.Actions.cs` | `e3d37e9432c2eec4419b23d865d92514f907c750c58a3dc9b5381852b2c67ff5` |
| `tests/MeowChan.DesktopTests/PluginErrorLanguageRefreshTests.cs` | `8ae67c01cb8cd30a1d7643b6a30905d815840445e7bfc1a5709fb3de7345b4c6` |

The report itself is intentionally excluded from its embedded hash table to avoid
a self-referential digest. Its final SHA-256 is emitted separately by the handoff
command. These five hashes do not bind unrelated WIP or prove that a binary was
built from these sources; root must reconcile its own run inputs and results.

### Added tests — authored, not executed

The new file contains **7 test methods / 15 declared cases** (3 theories with
6 + 2 + 3 InlineData rows, plus 4 facts), counted from source, not test discovery:

| Coverage | Declared cases | Result here |
| --- | ---: | --- |
| Host keys rerender EN → ES/FR/RU → EN without losing provenance | 6 | Not run |
| Build/editor wrappers preserve raw arguments and copy the input array | 2 | Not run |
| Raw key-looking, multiline and empty strings remain verbatim | 3 | Not run |
| English-translation collision is not reverse-mapped | 1 | Not run |
| Callback dispatch/lifetime guard and shared text/focus/automation/visibility wiring | 1 | Not run |
| Explicit raw/localized replacement and null-clear wiring | 1 | Not run |
| Host/action call-site provenance and retained raw failure paths | 1 | Not run |

No compile, test discovery, focused run or full Desktop run was performed by this
worker. There are **no passing-test counts** to report. CLR-state cases and source
contracts do not substitute for a live page/dispatcher/banner integration test.

### Final static findings and disposition

- **L8-01, stale app-owned banner text:** implementation addressed at
  `PluginsPage.axaml.cs:49–66,115–129,497–517`. State stores explicit provenance;
  language refresh only rerenders it. Runtime verification remains pending.
- **App-owned call sites:** key-backed in `Testing.cs:56,121,182,226`,
  `Migration.cs:30`, main-page unavailable handling at `axaml.cs:177`, and action
  rejection at `Actions.cs:155–158`. Action keys are resolved before translation
  (`Actions.cs:58–92`), without changing existing rejection predicates.
- **Failure retention/accessibility:** `RenderError()` projects the retained
  message to Text, Focusable, automation Name and banner IsVisible. It does not
  mutate the error state, rerun workers or clear an existing failure. Existing
  operation/selection lifecycle clears are intentionally unchanged.
- **Raw provenance:** exception, field-validation and probe-failure strings stay
  on `Error(string?)`; no translation-key inference or reverse mapping was added.
  Compiler failure status is an argument to the localized host wrapper; compiler
  diagnostic messages are untouched.
- **No additional concrete defect identified in the bounded static inspection.**
  This is the implementation owner's source review, not independent review,
  compilation success, a P0/P1/P2-wide audit or runtime acceptance.

### Remaining boundaries / root handoff

- Product and test code are stable for root's full Desktop run. Do not infer a
  successful run or source/binary reconciliation from this report.
- L8-02 probe output/caching, reader groups, destructive/repair dialogs, language
  pack quality and native Windows/macOS behavior remain outside this patch.
- Raw failures deliberately do not change language; only host-owned wrappers do.
  Arguments are shallow-copied (sufficient for current string/status arguments),
  not generalized snapshots of potentially mutable future argument objects.
- The source-contract tests check wiring, not live focus retention or dispatcher
  execution. Catalog reload rendering is source-backed, not runtime-tested here.
- No build/test/Git/RUNSTATE/snapshot/source-freeze/cleanup operations were made
  during implementation or this report-only finalization.
