# Shared empty-overflow contract (2026-11-18)

Base: `origin/goal/gesamtprogramm-profile-a`, `89021dd7e`.
Finding: DL-05 (overflow only real actions), with DL-06 / IN-11 continuity preserved.

## Root cause and ownership

`ActionOverflowLayout` already filters invisible actions correctly. `WorkViewPresenter`
projects a hidden work-view action through `ActionToolBar.Omitted`; state/context/permission
owners provide `IsVisible`. `WorkViewMoreSurface` (the separate page-menu affordance)
already configures visibility from its entry count and rejects empty opens. Those policies
and other ellipses with real actions do not need a new hide rule.

The shared toolbar's *lifecycle* was incomplete: visibility/omission handlers invalidated
measurement, but its cached projected count and open popup survived until layout. Removing
the last item also left the count stale. A stale/automation activation could check the
hidden More toggle with zero entries and drive the template's popup open. The existing
arithmetic/source-string tests could not prove this affordance contract.

The fix belongs in `ActionToolBar` / `ActionToolBarPanel`, not individual pages or a second
work-view/permission policy. The panel revalidates its measured projection against live
visibility, omission, and current collection membership; width/priority placement remains
owned by measurement. The toolbar synchronizes its toggle, pseudo-class, accessible count,
and popup with that result, including template initialization and stale activation.

**Contract:** zero eligible projected action entries means hidden More, unchecked opener,
and closed popup. Hidden/permission-filtered and view-omitted actions do not count. Enabled
state is deliberately **not** an eligibility filter: this app retains disabled actions and
their explanations. It reparents the original control, preserving command identity, disabled
reason and gestures. The toolbar itself is never hidden just because its actions are disabled.

## Behavioral coverage

`tests/MeowChan.DesktopTests/ActionToolBarOverflowContractTests.cs` constructs the installed
Ursa/Semi toolbar template with the real `ActionToolBar`, panel, layout policy and controls.
It verifies:

1. Zero actions, including stale activation: no visible opener or empty popup.
2. One action: the original action remains reachable and invokes through its automation peer.
3. Hidden/permission-filtered inputs and `Omitted` inputs do not count.
4. Disabled explanatory action remains discoverable, with its original tooltip.
5. Three hide/show cycles close an empty popup, restore the action, reuse the same toggle,
   panel and action, preserve direct-action bounds, and produce exactly six visibility
   transitions (no layout oscillation/control replacement).
6. Filtering one of two actions leaves More and its nonempty popup open; accessible count is one.
7. Removing the last action closes More immediately.
8. Omitting/restoring the last action updates More and parks/restores the original control.
9. Changing OverflowMode invalidates the actual panel at unchanged width and changes placement.

The last case replaces only the related source-substring invalidation test in
`ActionOverflowLayoutTests.cs`. It still checks invalidation explicitly, then additionally
checks visible state and actual control placement in both directions. Other layout and
source-wiring assertions are unchanged.

## Validation receipts and limits

Standard desktop tests were attempted before the production edit:

```sh
dotnet test tests/MeowChan.DesktopTests/MeowChan.DesktopTests.csproj \
  --filter FullyQualifiedName~ActionToolBarOverflowContractTests
```

The base cannot compile: `Desktop/Views/Media/MediaPage.cs:220` references missing
`_contentHeader` (CS0103), and `MediaPage.Selection.cs:395` reports CA2000 for
`options.NewSession(QuickTagTarget.For(session))`. Neither is in the permitted product
write scope. Neither was edited or suppressed. Consequently the normal DesktopTests
assembly could not execute; integration validation remains **incomplete**, not green.
The after-fix focused desktop build also ends with exactly those two existing errors:

```sh
dotnet build MeowChan.csproj --no-restore \
  -p:MeowChanPluginDeliveryEnabled=false -m:2 -v quiet
```

Its receipt is `$COMMANDCODE_SCRATCHPAD/overflow-desktop-build.log`. Plugin delivery was
excluded only from this focused build, not changed in project configuration.

Supplemental isolated validation links the exact production toolbar/panel/layout source,
real theming sources, the exact new tests, existing `ActionOverflowLayoutTests`, and existing
`ActionToolBarRenderedStretchTests` into a temporary test project. It uses the same package
versions. Only test dispatcher/cursor infrastructure and a label-only localization seam
are supplied separately; no overflow/projection behavior is stubbed. No native window is
opened. `Popup.IsOpen` assertions exercise the installed template binding, not OS input.

| Receipt | Result |
| --- | --- |
| Same final nine behavior tests against base `ActionToolBar.cs` | **5 fail / 4 pass** |
| Same nine behavior tests against fixed shared source | **9 pass** |
| New behavior + existing layout/rendered toolbar cases | **44 pass / 0 fail** |
| Isolated focused build with analyzers and CA2000/CA2213/CA1001/CA2016 as errors | **0 errors** (existing non-blocking warnings remain) |

Session evidence lives under `$COMMANDCODE_SCRATCHPAD`: `overflow-before.log` (normal
DesktopTests build blockers), `overflow-linked-before-final.log`,
`overflow-linked-build.log`, and `overflow-linked-all-after.log`. The temporary
`overflow-focused/` project records the isolated source list and infrastructure seams.

The touched production and existing test files have no entries in
`build/analyzer-baseline-2026-11-17.tsv`; no baseline rows, hashes, analyzer settings,
or suppressions were changed. New tests introduce no disposable ownership beyond their
explicitly disposed fixture/locator scopes.

No full solution build, GUI smoke suite, screenshot/native-input acceptance, localization
certification, or permission-service integration test was run. Permission coverage here
means the toolbar's **post-filter projection inputs**, not a new authorization policy.

The requested external worktree path was refused by the workspace boundary. The same
requested branch was created from the fetched goal base in the permitted isolated worktree
`.commandcode/wt/empty-overflow-menu`; the main worktree's existing changes were untouched.
