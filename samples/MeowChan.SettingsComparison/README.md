# URSA-17 / URSA-22 executable comparison

Non-shipped experiment. No product default switch. It references the checkout's real `SettingsEditor`, so run the final section comparison only after W09 notification settings and W13 shortcuts have been integrated into that checkout. The standalone sample generates its profiles beneath the OS temporary directory, passes them explicitly and removes them when its window closes. The integrated Smoke scenario below instead uses the runner's disposable profile; launch it through that isolated runner.

Only W20 may execute these recipes during the coordinated run. Every .NET/GUI invocation holds `/tmp/meowchan-ui-1.0-build.lock`.

```sh
flock /tmp/meowchan-ui-1.0-build.lock dotnet build samples/MeowChan.SettingsComparison/MeowChan.SettingsComparison.csproj -c Release
flock /tmp/meowchan-ui-1.0-build.lock dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj -c Release --filter FullyQualifiedName~LocalFolderPickerTests
flock /tmp/meowchan-ui-1.0-build.lock dotnet samples/MeowChan.SettingsComparison/bin/Release/net10.0/MeowChan.SettingsComparison.dll --numeric-matrix > numeric-matrix.json
flock /tmp/meowchan-ui-1.0-build.lock dotnet samples/MeowChan.SettingsComparison/bin/Release/net10.0/MeowChan.SettingsComparison.dll
```

Use W20's private Xvfb/Openbox/DBus/XDG launch environment for automated GUI execution; manual IME and native AT runs require the platform's real input stack. The matrix prints JSON observations. Exit 0 means the runner completed; inspect every `rawGate`, `saveGate`, `requestedFocused`, and the explicit unrun IME/AT entries. Variant failures are evidence, not a reason to suppress or rewrite their rows.

## URSA-22

Choose RecheckTimer and A/B/C. A is the shipped `SettingsNumberInput`. B is `NumericIntUpDown` plus a bounded adapter overriding protected `CommitInput` and preserving direct spinner focus. C removes the existing subclasses and uses unmodified Avalonia `NumericUpDown`, testing the proposed simplification. Equal dimensions, bounds, unit, parser policy and save/discard actions apply. Changing variants resets that experimental field to the committed test config.

`--numeric-matrix` runs 13 fixtures × 2 cultures × 3 variants, plus culture changes during invalid input and direct spinner-focus observations. It records input, raw text before/after blur and Save, current Value, config before/after, focus, and visible error. Each accepted Save writes the isolated config and invalid input must preserve the config. For persisted/reloaded verification, successful runs reload the explicit test profile. Native IME and AT are marked unrun, never inferred from a screenshot.

Manual checks for each A/B/C: empty/space/minus/letters/80-digit paste; selection replacement, Home/End, Undo; mouse and keyboard spinner at 10 and 4294967; invalid raw → blur → refocus → Save → correct; invalid raw → switch en-US/de-DE; EN/DE app labels independently; discard and reopen; save failure checkbox → Save → correction/retry. Start, finish and cancel an actual IME composition. Read field, unit, bounds, enabled state, spinner names and error help using the real platform screenreader. Capture a row per task and fail B on any raw loss, silent correction, stale-value Save, focus/AT regression or boundary mismatch. Keep A without a proven advantage; C is only a simplification experiment.

## URSA-17

Choose Integrated Settings. A hides the navigation and leaves the exact same headings/fields. B is the implemented explicit contents with wide wrapping links and a compact selector below 560 DIP actual navigation width. C uses actual Ursa `Anchor` in the same explicitly opened area, intercepting its public pointer override and Enter/Space route so only the shared app controller scrolls. C falls back to the exact B compact content below 560 DIP. No private reflection, synthesized pointer or parallel form copy.

Run with final W09/W13 settings present. For every A/B/C, use identical initial fields, window, theme, language, raw input and task sequence:

1. Find Updates; count scroll/activation steps and wrong targets.
2. Enter invalid timer/download raw text; reach Shortcuts, Notifications, then Downloads; verify unchanged raw/model/config and no Save.
3. Open worker budgets, navigate to Updates, back to budgets; other expanders stay open.
4. Save invalid timer in a collapsed group; actual text editor receives error focus. Navigate away intentionally; old error reveal must not scroll back. Change language and size without navigation; no focus or draft change.
5. End-of-scroller target: heading and first control reachable, footer visible. Repeat opening/closing and attach/detach; no duplicate jumps.

Record step counts, focus target, wrong destination, raw/config, label wrapping/clipping, usable content height, screenshots and real AT observations. Same fixtures at EN/DE × Dark/Light × B 640×480/S/A 760×500 × scale 1/1.5/2; test immediately either side of 560-DIP detail width. C must improve two long tasks over B without any hard regression to replace B. Short pages may retain A. The implemented default B remains provisional until this integrated comparison is actually recorded.

Plugin details use the same controller in the product. The existing `PluginManagementChecks` fixture must run after W06/W07 integration: three source scopes including duplicate display names; active refresh; schema/source removal while contents open; no-parameter plugin; long help/errors and back-to-list. Verify candidate-key/source-ID destinations, stale target notice, error-field priority, no network/save/discard and unchanged drafts. Compare the plugin page A/B/C using the same task/observation sheet; do not substitute Settings results for plugin evidence.

### Integrated Settings AND Plugins comparison scenario

`tests/MeowChan.DesktopSmoke/SectionComparisonChecks.cs` is the minimal test-only connection requested by W19 protocol §17. It hosts the checkout's actual `SettingsEditor` and `PluginsPage`, using their actual section descriptors and actual form/draft/editor instances. A hides contents, B retains product contents, C replaces only the navigation projection with Ursa Anchor and reuses B below 560 DIP. The toolbar can switch A/B/C on one unchanged form. Read-only reflection accesses the two app-owned controller fields; it does not access private Ursa/Avalonia internals. No product switch, second form implementation or store implementation is introduced.

Only W20 builds/runs this scenario in the coordinated run, after integration into a new frozen source snapshot. Run from that snapshot, substituting its actual digest and a fresh evidence directory:

```sh
flock /tmp/meowchan-ui-1.0-build.lock dotnet build tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj -c Release
env MEOWCHAN_SECTION_COMPARISON_ONLY=1 MEOWCHAN_COMPARISON_SOURCE_DIGEST=<new-frozen-source-digest> flock /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode normal --scale 1 --timeout 300 --output <new-evidence-directory>
```

The angle-bracket placeholders are recipe notation, not literal shell arguments. Keep W20's private Xvfb/Openbox/DBus/XDG environment. The new scenario uses isolated profile subdirectories, actual `PluginProfileStore` and inert worker/compiler boundaries; no worker build/probe/network operation is part of navigation. The plugin fixture declares three distinct source IDs, two identical display names, eight plugin fields, nine fields per source, long help and a filter making the real Grants section available. The short fixture has one valid source and no configurable fields.

The automatic task order is Settings raw roundtrip A/B/C, Settings budgets/error C/B/A, plugin scope/error B/C/A, short plugin A. It starts EN/Dark/Advanced, 1100×760 DIP, and performs the narrow Plugins Back check at 760×500. A/B/C use identical fields/expansion state within each task. It checks raw/config preservation, actual error-field focus, refresh/language retention, persisted profile hashes before the explicit catalog-removal fixture event, source identity/removal and a 640→540→640 navigation focus regression. The last regression requires W13's integrated breakpoint-focus fix; FULL06 alone does not establish a pass.

Each `SECTION_COMPARISON {...}` line records the source/fixture digest, prediction, actual event, destination, focus, scroll geometry, retained draft/model/profile data or result. Counts come from events actually dispatched; there are no expected hardcoded action counts. Button actions are **in-process routed ClickEvents**; key steps are **in-process routed KeyDown/KeyUp**. Text edits use `TextBox.Text` and are disclosed as single in-process edit steps. `Focus()` targets these inputs and is explicitly **not a performed Tab route**. Coarse scrolling uses actual PageUp/PageDown key events; fine scrolling uses separately recorded **in-process routed wheel events on the actual ScrollContentPresenter**, with delta, handled state, focus and before/after offset. Avalonia 12's ScrollViewer handles page keys rather than arrow keys. A nonmoving scroller still fails; no direct-offset fallback or invented scroll count is substituted. Initial scroll reset, catalog transitions and resize/focus regression setup are fixture events, separately labeled. Captures use the existing runner's `smoke-*.png` collection prefix.

The runner's completion/exit 0 means the harness finished. Inspect **every `kind=task-result`** and its `data.result`; caught task failures remain failures even when the runner completes. Review destination/result screenshots (existing `SMOKE window render` paths), actual heading bounds and viewport records for wrapping/clipping; the harness marks visual clipping unassessed. A retained dirty form is distinct from a removed source remaining offered by contents; B/C fail that gate rather than passing solely because raw text survived. The catalog-removal fixture deliberately writes candidate metadata after the normal navigation persistence gate.

For a real observer, also set `MEOWCHAN_SECTION_COMPARISON_INTERACTIVE=1`: the suite opens one real Settings form, then one Plugins form; close each to continue. Use A/B/C toolbar buttons without rebuilding the form, enter a prediction, and use “Record observation” for a checkpoint and screenshot. Run with a disposable native desktop through the existing runner, an explicit native-display opt-in and a suitable timeout; this is the path for actual keyboard/IME/AT observation. The automatic minimal run does not cover native Tab order, screenreader, IME, human orientation or preference. EN/DE × Dark/Light × B/S/A minima, scale 1.5×/2× and native platform gates remain separately required by the package/protocol. C may replace B only after an observed advantage on both long tasks without a hard regression; until then B remains provisional. URSA-22's existing 78-row numeric matrix is unchanged and remains a separate comparison.

### Optional production-B presentation matrix (09/17)

After the core section scenario succeeds, W20 can run the bounded matrix separately by adding `MEOWCHAN_SECTION_PRESENTATION_ONLY=1` to the existing section entry. It reuses exactly the Settings/Plugins fixtures, section controller and event driver above. It runs **12 real cells** (EN/DE × Light/Dark × Barebones640×480 / Simple760×500 / Advanced760×500), each with one Settings and one Plugins task. It does **not** repeat the ten A/B/C comparisons per cell. The first failing presentation task records its exception/failure capture and stops the matrix.

```sh
env MEOWCHAN_SECTION_COMPARISON_ONLY=1 MEOWCHAN_SECTION_PRESENTATION_ONLY=1 MEOWCHAN_COMPARISON_SOURCE_DIGEST=<new-frozen-source-digest> flock /tmp/meowchan-ui-1.0-build.lock python3 scripts/run-desktop-smoke.py --assembly tests/MeowChan.DesktopSmoke/bin/Release/net10.0/MeowChan.DesktopSmoke.dll --mode normal --scale 1 --timeout 600 --output <new-section-presentation-evidence>
```

W20 repeats only this bounded presentation recipe at `--scale 1.5` and `--scale 2`, using new evidence directories. Main-window mode uses the existing disposable-profile presentation helper; global language/theme application is explicit fixture setup. Each child hosts the actual page without comparison-toolbar overhead. `presentation-observation` records actual persisted workspace mode, rendered localized text/bounds, actual theme/language, client/frame/minimum dimensions, RenderScaling, focus, selected stable ID, viewport and retained state. Requested labels alone are insufficient: actual localization, ActualThemeVariant and measured minimum dimensions are asserted.

Each page captures three representative scenes: natural-width opened contents at the real window minimum; focused compact Go with actual translated label; and local validation error on the actual input. The compact subscene **explicitly constrains navigation MaxWidth to 540 DIP**, checks actual width below560 and activates the real selector/Go using routed keys. It does not pretend every natural window minimum enters compact mode. Go focus uses disclosed harness `Focus()` targeting, then actual Enter must close contents and jump/focus the exact heading; this is not a native Tab-order result. Settings retains a relative Unicode path over navigation, then shows the existing empty-path Save error without a commit. Plugins retains invalid source-b raw text over information/back navigation despite duplicate source names, then shows/focuses its real Save error without a worker or profile metadata change. Successful coverage is 24 task-results and72 captures per scale; inspect the actual records/images, not that expected count alone. Path error scenes exercise manual input/local validation, with native picker explicitly unrun. Existing semantic source-removal/refresh/resize comparisons, numeric78, shortcuts capture/help, and native AT/IME remain separate.
